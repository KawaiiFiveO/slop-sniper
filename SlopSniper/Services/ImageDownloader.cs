namespace SlopSniper.Services;

/// <summary>Downloads images from Discord's CDN and media proxy. Images are only ever held in memory.</summary>
public sealed class ImageDownloader(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "discord-cdn";

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
}
