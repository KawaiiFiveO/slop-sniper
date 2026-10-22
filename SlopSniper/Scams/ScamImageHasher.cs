using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <param name="Hash">Null if the image couldn't be fetched or decoded.</param>
/// <param name="Error">Why there's no hash.</param>
public sealed record ImageHash(ImageSource Image, ulong? Hash, string? Error, TimeSpan Elapsed);

/// <summary>Hashes a post's images from tiny copies resized by Discord's media proxy: a few KB each, so hashing every
/// image post costs next to nothing.</summary>
public sealed class ScamImageHasher(ImageDownloader downloader, ILogger<ScamImageHasher> logger)
{
    /// <summary>Scam posts are usually 3–4 screenshots; any one of them is enough to recognise.</summary>
    public const int MaxImages = 4;

    // Twice the hash's 32×32 grid, so the proxy's resampling doesn't decide the result.
    private const int DownloadSize = 64;

    /// <returns>The hashes of the images that could be fetched and decoded.</returns>
    public async Task<IReadOnlyList<ulong>> HashAsync(IReadOnlyList<ImageSource> images, CancellationToken cancellationToken)
    {
        List<ulong> hashes = [];
        foreach (var image in images.Take(MaxImages))
        {
            if ((await HashImageAsync(image, cancellationToken)).Hash is { } hash)
            {
                hashes.Add(hash);
            }
        }
        return hashes;
    }

    public async Task<ImageHash> HashImageAsync(ImageSource image, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (await downloader.DownloadResizedAsync(image, DownloadSize, cancellationToken) is not { } bytes)
            {
                return new ImageHash(image, null, "no media proxy URL, or the copy was too large", stopwatch.Elapsed);
            }
            return PerceptualHash.Compute(bytes) is { } hash
                ? new ImageHash(image, hash, null, stopwatch.Elapsed)
                : new ImageHash(image, null, "couldn't decode the image", stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't fetch {Image} for hashing: {Error}", image.LogName, ex.Message);
            return new ImageHash(image, null, $"couldn't fetch it: {ex.Message}", stopwatch.Elapsed);
        }
    }
}
