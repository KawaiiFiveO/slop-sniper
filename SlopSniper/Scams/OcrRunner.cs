using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <param name="Text">What tesseract read, or null if it couldn't. Never logged or stored.</param>
/// <param name="Error">Why there's no text.</param>
public sealed record OcrResult(ImageSource Image, string? Text, ScamWords Groups, IReadOnlyList<string> Keywords, string? Error, TimeSpan Elapsed);

/// <summary>The word groups found across a post's images.</summary>
public sealed record OcrScan(ScamWords Groups, IReadOnlyList<string> Keywords, IReadOnlyList<OcrResult> Images)
{
    public static readonly OcrScan Empty = new(ScamWords.None, [], []);
}

/// <summary>Reads text in images with tesseract, run as a child process so its memory is returned as soon as it
/// exits. One image at a time, on one thread, with a time limit: the VPS is small and shared. Only used for posts that
/// already look suspicious, unless Scams.OcrAllImages is on.</summary>
public sealed class OcrRunner(ImageDownloader downloader, IOptionsMonitor<BotOptions> options, ILogger<OcrRunner> logger)
{
    /// <summary>Scam posts are usually 3–4 screenshots; reading more costs time for nothing.</summary>
    public const int MaxImages = 4;

    // Phone screenshots are tall; this keeps their text large enough to read.
    private const int MaxSide = 1600;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _probedCommand;
    private string? _version;
    private string? _probeError;
    private long _reads;

    /// <summary>For /sniper status.</summary>
    public string Status =>
        options.CurrentValue.Scams.OcrCommand.Length == 0 ? "off"
        : _version is not null ? $"{_version}, {Interlocked.Read(ref _reads)} images read"
        : _probeError is not null ? $"not available ({_probeError})"
        : "not checked yet";

    /// <summary>Whether OcrCommand runs. Checked once, and again after the command changes.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        var command = options.CurrentValue.Scams.OcrCommand;
        if (command.Length == 0)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (command != _probedCommand)
            {
                _probedCommand = command;
                var (output, error) = await RunAsync(command, ["--version"], null, ProbeTimeout, cancellationToken);
                _version = error is null ? FirstLine(output) ?? command : null;
                _probeError = error;
                if (_version is not null)
                {
                    logger.LogInformation("OCR: {Version}", _version);
                }
                else
                {
                    logger.LogWarning("OCR isn't available, so scam detection won't read text in images: {Error}", _probeError);
                }
            }
            return _version is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="stopAtGroups">Stop reading more images once this many word groups are found.</param>
    public async Task<OcrScan> ScanAsync(IReadOnlyList<ImageSource> images, int stopAtGroups, CancellationToken cancellationToken)
    {
        var groups = ScamWords.None;
        List<string> keywords = [];
        List<OcrResult> results = [];
        foreach (var image in images.Take(MaxImages))
        {
            var result = await ReadAsync(image, cancellationToken);
            results.Add(result);
            groups |= result.Groups;
            keywords.AddRange(result.Keywords);
            if (ScamKeywords.Count(groups) >= stopAtGroups)
            {
                break;
            }
        }
        return new OcrScan(groups, keywords.Distinct().ToList(), results);
    }

    public async Task<OcrResult> ReadAsync(ImageSource image, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        OcrResult Failed(string error) => new(image, null, ScamWords.None, [], error, stopwatch.Elapsed);

        byte[]? png;
        try
        {
            if (await downloader.DownloadResizedAsync(image, MaxSide, cancellationToken) is not { } bytes)
            {
                return Failed("no media proxy URL, or the copy was too large");
            }
            png = Prepare(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't fetch {Image} for OCR: {Error}", image.LogName, ex.Message);
            return Failed($"couldn't fetch it: {ex.Message}");
        }
        if (png is null)
        {
            return Failed("couldn't decode the image");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Sparse text (--psm 11) finds the scattered labels of app screenshots.
            var (text, error) = await RunAsync(options.CurrentValue.Scams.OcrCommand, ["stdin", "stdout", "--psm", "11", "-l", "eng"],
                png, ReadTimeout, cancellationToken);
            if (error is not null)
            {
                logger.LogWarning("OCR failed for {Image}: {Error}", image.LogName, error);
                return Failed(error);
            }

            Interlocked.Increment(ref _reads);
            var (groups, keywords) = ScamKeywords.Find(text);
            return new OcrResult(image, text, groups, keywords, null, stopwatch.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Grayscale PNG with dark text on a light background, which tesseract reads best: dark mode
    /// screenshots are inverted.</summary>
    /// <returns>Null if the image can't be decoded.</returns>
    public static byte[]? Prepare(ReadOnlySpan<byte> image)
    {
        using var decoded = ImageDecoder.TryDecode(image, MaxSide);
        if (decoded is null)
        {
            return null;
        }

        // Premultiplied RGBA: transparent pixels are composited onto white.
        ReadOnlySpan<byte> pixels = decoded.GetPixelSpan();
        var gray = new byte[decoded.Width * decoded.Height];
        long sum = 0;
        for (var i = 0; i < gray.Length; i++)
        {
            var p = pixels.Slice(i * 4, 4);
            var value = (int)(0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]) + (255 - p[3]);
            gray[i] = (byte)Math.Min(255, value);
            sum += gray[i];
        }

        if (sum < 128L * gray.Length)
        {
            for (var i = 0; i < gray.Length; i++)
            {
                gray[i] = (byte)(255 - gray[i]);
            }
        }

        using var result = SKImage.FromPixelCopy(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Gray8, SKAlphaType.Opaque), gray);
        using var encoded = result?.Encode(SKEncodedImageFormat.Png, 100);
        return encoded?.ToArray();
    }

    /// <returns>Standard output, or why the command failed.</returns>
    private static async Task<(string Output, string? Error)> RunAsync(
        string command, IReadOnlyList<string> arguments, byte[]? input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Tesseract would otherwise use every core of a VPS that's busy with other things.
        start.Environment["OMP_THREAD_LIMIT"] = "1";

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            // ENOENT on Linux, ERROR_FILE_NOT_FOUND on Windows.
            return ("", ex.NativeErrorCode == 2 ? $"{command} not found" : $"couldn't run {command}: {ex.Message}");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errors = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, timeoutSource.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeoutSource.Token);
            var (stdout, stderr) = (await output, await errors);
            return process.ExitCode == 0 ? (stdout, null) : ("", $"exited with code {process.ExitCode}: {FirstLine(stderr)}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ("", $"took longer than {timeout.TotalSeconds:0} s");
        }
        catch (IOException ex)
        {
            // It exited before reading all of its input.
            return ("", ex.Message);
        }
        finally
        {
            Kill(process);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
