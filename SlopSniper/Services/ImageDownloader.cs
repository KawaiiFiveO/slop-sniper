using System.Globalization;

namespace SlopSniper.Services;

/// <summary>Downloads images from Discord's CDN and media proxy. Images are only ever held in memory.</summary>
public sealed class ImageDownloader(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "discord-cdn";

    // A resized copy is small, but cap it anyway in case the proxy ignores the size.
    private const long MaxResizedBytes = 8 * 1024 * 1024;

    /// <returns>The file, or null if it turns out to be larger than <paramref name="maxBytes"/>.</returns>
    public async Task<byte[]?> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
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

    /// <summary>A copy that fits in <paramref name="maxSide"/> pixels, resized by Discord's media proxy so the bot
    /// never downloads or decodes the full image.</summary>
    /// <returns>The file, or null if the image has no proxy URL or is too large.</returns>
    public Task<byte[]?> DownloadResizedAsync(ImageSource image, int maxSide, CancellationToken cancellationToken) =>
        GetResizedUrl(image, maxSide) is { } url
            ? DownloadAsync(url, MaxResizedBytes, cancellationToken)
            : Task.FromResult<byte[]?>(null);

    /// <summary>The proxy URL with the target size added, keeping the aspect ratio when the size is known.</summary>
    public static string? GetResizedUrl(ImageSource image, int maxSide)
    {
        if (!ImageFormats.IsDiscordProxyUrl(image.ProxyUrl))
        {
            return null;
        }

        var (width, height) = (maxSide, maxSide);
        if (image is { Width: > 0 and var w, Height: > 0 and var h })
        {
            // Never upscale.
            var scale = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
            (width, height) = (Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
        }

        var url = image.ProxyUrl!;
        var separator = !url.Contains('?') ? "?" : url.EndsWith('?') || url.EndsWith('&') ? "" : "&";
        return string.Create(CultureInfo.InvariantCulture, $"{url}{separator}width={width}&height={height}");
    }
}
