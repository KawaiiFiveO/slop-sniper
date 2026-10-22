using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Provenance;

public enum CheckStatus
{
    Checked,
    TooLarge,
    Unsupported,
    Failed,
}

/// <param name="Detail">The size, type or error behind a status other than <see cref="CheckStatus.Checked"/>.</param>
public sealed record ImageCheck(
    ImageSource Image,
    CheckStatus Status,
    ProvenanceCheck? Result = null,
    bool FromCache = false,
    string? Detail = null)
{
    public bool Detected => Result?.Detected == true;
}

/// <summary>Checks one image: filters it, downloads it, and asks OpenAI unless the result is cached. Every request
/// goes through one throttle shared by automatic and manual checks.</summary>
public sealed class ImageChecker(
    ImageDownloader downloader,
    ProvenanceClient provenance,
    ResultCache cache,
    OpenAiStatus status,
    Reporter reporter,
    TimeProvider time,
    IOptionsMonitor<BotOptions> options,
    ILogger<ImageChecker> logger)
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan[] TransientBackoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuotaRetryDelay = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    public async Task<ImageCheck> CheckAsync(ImageSource source, CancellationToken cancellationToken)
    {
        if (!ImageFormats.IsSupported(source))
        {
            return new ImageCheck(source, CheckStatus.Unsupported, Detail: ImageFormats.Describe(source));
        }

        var maxBytes = options.CurrentValue.Provenance.GetMaxImageBytes();
        if (source.Size > maxBytes)
        {
            return new ImageCheck(source, CheckStatus.TooLarge, Detail: FormatMiB(source.Size.Value));
        }

        byte[]? image;
        try
        {
            image = await downloader.DownloadAsync(source.Url, maxBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't download {Image}: {Error}", source.LogName, ex.Message);
            return new ImageCheck(source, CheckStatus.Failed, Detail: "couldn't download the image from Discord");
        }

        if (image is null)
        {
            return new ImageCheck(source, CheckStatus.TooLarge, Detail: $"over {options.CurrentValue.Provenance.MaxImageMiB} MiB");
        }

        if (ImageFormats.Sniff(image) is not { } mediaType)
        {
            return new ImageCheck(source, CheckStatus.Unsupported, Detail: "not a PNG, JPEG or WebP file");
        }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (cache.TryGet(hash, out var cached))
        {
            return new ImageCheck(source, CheckStatus.Checked, cached, FromCache: true);
        }

        try
        {
            var result = await SendWithRetriesAsync(image, mediaType, cancellationToken);
            cache.Set(hash, result);
            if (status.ReportSuccess())
            {
                logger.LogInformation("OpenAI checks resumed.");
                await reporter.SendAlertAsync("OpenAI provenance checks are working again.");
            }
            return new ImageCheck(source, CheckStatus.Checked, result);
        }
        catch (ProvenanceException ex)
        {
            await HandleFailureAsync(source, ex);
            return new ImageCheck(source, CheckStatus.Failed, Detail: ex.Message);
        }
    }

    private async Task HandleFailureAsync(ImageSource source, ProvenanceException ex)
    {
        logger.LogWarning("OpenAI check failed for {Image} ({Failure}): {Error}", source.LogName, ex.Failure, ex.Message);

        var suspension = ex.Failure switch
        {
            ProvenanceFailure.QuotaExceeded => (Retry: (TimeSpan?)QuotaRetryDelay,
                Alert: $"OpenAI provenance checks are paused: the API quota is used up. Trying again hourly.\n> {ex.Message}"),
            ProvenanceFailure.AccessDenied => (Retry: null,
                Alert: $"OpenAI provenance checks are paused: the API key was rejected or lost access. Fix Provenance.OpenAiApiKey, then `/sniper-reload` or restart.\n> {ex.Message}"),
            _ => default,
        };

        if (suspension.Alert is not null && status.Suspend(ex.Message, suspension.Retry))
        {
            logger.LogError("OpenAI checks suspended: {Error}", ex.Message);
            await reporter.SendAlertAsync(suspension.Alert);
        }
    }

    private async Task<ProvenanceCheck> SendWithRetriesAsync(byte[] image, string mediaType, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ThrottledAsync(() => provenance.CheckAsync(image, mediaType, cancellationToken), cancellationToken);
            }
            catch (ProvenanceException ex) when (ex.Failure == ProvenanceFailure.RateLimited && attempt < MaxAttempts)
            {
                // ThrottledAsync has already pushed the next request back by the server's Retry-After.
                logger.LogWarning("OpenAI rate limit hit; retrying (attempt {Attempt} of {MaxAttempts}).", attempt + 1, MaxAttempts);
            }
            catch (ProvenanceException ex) when (ex.Failure == ProvenanceFailure.Transient && attempt < MaxAttempts)
            {
                var delay = TransientBackoff[attempt - 1];
                logger.LogWarning("{Error} Retrying in {Delay}s.", ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, time, cancellationToken);
            }
        }
    }

    /// <summary>Runs one request at a time, at least RequestIntervalSeconds apart. A rate-limit response pushes the
    /// next request back for every caller.</summary>
    private async Task<T> ThrottledAsync<T>(Func<Task<T>> send, CancellationToken cancellationToken)
    {
        await _throttle.WaitAsync(cancellationToken);
        try
        {
            var wait = _nextRequestAt - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, cancellationToken);
            }

            try
            {
                return await send();
            }
            catch (ProvenanceException ex) when (ex.Failure == ProvenanceFailure.RateLimited)
            {
                _nextRequestAt = time.GetUtcNow() + (ex.RetryAfter ?? DefaultRateLimitWait);
                throw;
            }
            finally
            {
                var earliest = time.GetUtcNow() + TimeSpan.FromSeconds(options.CurrentValue.Provenance.RequestIntervalSeconds);
                if (earliest > _nextRequestAt)
                {
                    _nextRequestAt = earliest;
                }
            }
        }
        finally
        {
            _throttle.Release();
        }
    }

    public static string FormatMiB(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MiB");
}
