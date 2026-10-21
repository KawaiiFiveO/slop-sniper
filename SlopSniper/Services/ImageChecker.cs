using System.Globalization;
using System.Security.Cryptography;
using Discord;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Provenance;

namespace SlopSniper.Services;

public enum CheckStatus
{
    Checked,
    TooLarge,
    Unsupported,
    Failed,
}

/// <param name="Detail">The size, type or error behind a status other than <see cref="CheckStatus.Checked"/>.</param>
public sealed record AttachmentCheck(
    IAttachment Attachment,
    CheckStatus Status,
    ProvenanceCheck? Result = null,
    bool FromCache = false,
    string? Detail = null)
{
    public bool Detected => Result?.Detected == true;
}

/// <summary>Checks one attachment: filters it, downloads it, and asks OpenAI unless the result is cached. Every
/// request goes through one throttle shared by automatic and manual checks.</summary>
public sealed class ImageChecker(
    IHttpClientFactory httpClientFactory,
    ProvenanceClient provenance,
    ResultCache cache,
    OpenAiStatus status,
    Reporter reporter,
    TimeProvider time,
    IOptionsMonitor<BotOptions> options,
    ILogger<ImageChecker> logger)
{
    public const string DownloadClientName = "discord-cdn";

    private const int MaxAttempts = 4;
    private static readonly TimeSpan[] TransientBackoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuotaRetryDelay = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    public async Task<AttachmentCheck> CheckAsync(IAttachment attachment, CancellationToken cancellationToken)
    {
        if (!ImageFormats.IsSupported(attachment))
        {
            return new AttachmentCheck(attachment, CheckStatus.Unsupported, Detail: ImageFormats.Describe(attachment));
        }

        var maxBytes = options.CurrentValue.GetMaxImageBytes();
        if (attachment.Size > maxBytes)
        {
            return new AttachmentCheck(attachment, CheckStatus.TooLarge, Detail: FormatMiB(attachment.Size));
        }

        byte[]? image;
        try
        {
            image = await DownloadAsync(attachment.Url, maxBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't download attachment {AttachmentId}: {Error}", attachment.Id, ex.Message);
            return new AttachmentCheck(attachment, CheckStatus.Failed, Detail: "couldn't download the image from Discord");
        }

        if (image is null)
        {
            return new AttachmentCheck(attachment, CheckStatus.TooLarge, Detail: $"over {options.CurrentValue.MaxImageMiB} MiB");
        }

        if (ImageFormats.Sniff(image) is not { } mediaType)
        {
            return new AttachmentCheck(attachment, CheckStatus.Unsupported, Detail: "not a PNG, JPEG or WebP file");
        }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (cache.TryGet(hash, out var cached))
        {
            return new AttachmentCheck(attachment, CheckStatus.Checked, cached, FromCache: true);
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
            return new AttachmentCheck(attachment, CheckStatus.Checked, result);
        }
        catch (ProvenanceException ex)
        {
            await HandleFailureAsync(attachment, ex);
            return new AttachmentCheck(attachment, CheckStatus.Failed, Detail: ex.Message);
        }
    }

    private async Task HandleFailureAsync(IAttachment attachment, ProvenanceException ex)
    {
        logger.LogWarning("OpenAI check failed for attachment {AttachmentId} ({Failure}): {Error}", attachment.Id, ex.Failure, ex.Message);

        var suspension = ex.Failure switch
        {
            ProvenanceFailure.QuotaExceeded => (Retry: (TimeSpan?)QuotaRetryDelay,
                Alert: $"OpenAI provenance checks are paused: the API quota is used up. Trying again hourly.\n> {ex.Message}"),
            ProvenanceFailure.AccessDenied => (Retry: null,
                Alert: $"OpenAI provenance checks are paused: the API key was rejected or lost access. Fix OpenAiApiKey, then `/sniper reload`.\n> {ex.Message}"),
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
                var earliest = time.GetUtcNow() + TimeSpan.FromSeconds(options.CurrentValue.RequestIntervalSeconds);
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

    /// <returns>The file, or null if it turns out to be larger than <paramref name="maxBytes"/>.</returns>
    private async Task<byte[]?> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(DownloadClientName);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static string FormatMiB(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MiB");
}
