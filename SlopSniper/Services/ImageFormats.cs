using Discord;

namespace SlopSniper.Services;

/// <summary>Finds the images in a message, and decides which of them the API accepts.</summary>
public static class ImageFormats
{
    private static readonly HashSet<string> SupportedTypes = ["image/png", "image/jpeg", "image/webp"];
    private static readonly HashSet<string> SupportedExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    // Image formats the API doesn't accept. Listed so they're reported as unsupported instead of being ignored.
    private static readonly HashSet<string> OtherImageExtensions = [".gif", ".avif", ".heic", ".heif", ".bmp", ".tif", ".tiff"];

    /// <summary>Image attachments, including those in forwarded messages, and optionally link-preview images.</summary>
    public static IReadOnlyList<ImageSource> GetImages(IMessage message, bool includeLinkPreviews)
    {
        var messages = WithForwarded(message).ToList();
        var attachments = messages
            .SelectMany(m => m.Attachments)
            .Where(IsImage)
            .Select(ImageSource.FromAttachment);
        var previews = includeLinkPreviews ? messages.SelectMany(m => m.Embeds).SelectMany(GetPreviewImage) : [];

        return attachments.Concat(previews).DistinctBy(i => i.Url).ToList();
    }

    /// <summary>Based on Discord's metadata, before downloading. The file itself is checked by <see cref="Sniff"/>.</summary>
    public static bool IsSupported(ImageSource image)
    {
        if (image.ContentType is { } contentType)
        {
            return SupportedTypes.Contains(contentType.Split(';')[0].Trim().ToLowerInvariant());
        }

        // Link previews often have no extension (e.g. ?format=jpg); those are decided after downloading.
        var extension = GetExtension(image.Name);
        return SupportedExtensions.Contains(extension) || !OtherImageExtensions.Contains(extension);
    }

    /// <summary>Describes an image's type for "unsupported type" messages.</summary>
    public static string Describe(ImageSource image) =>
        image.ContentType ?? (GetExtension(image.Name) is { Length: > 0 } extension ? extension : "unknown type");

    /// <summary>The media type from the file's signature, or null if it isn't PNG, JPEG or WebP.</summary>
    public static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }

    /// <summary>Only Discord's own media proxy is fetched, so a link can't make the bot request an arbitrary URL.</summary>
    public static bool IsDiscordProxyUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "media.discordapp.net"
            || (uri.Host.StartsWith("images-ext-", StringComparison.Ordinal) && uri.Host.EndsWith(".discordapp.net", StringComparison.Ordinal)));

    private static bool IsImage(IAttachment attachment) =>
        attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
        || SupportedExtensions.Contains(GetExtension(attachment.Filename))
        || OtherImageExtensions.Contains(GetExtension(attachment.Filename));

    /// <summary>The message, then the messages it forwards.</summary>
    public static IEnumerable<IMessage> WithForwarded(IMessage message)
    {
        yield return message;
        if (message is IUserMessage { ForwardedMessages: { } snapshots })
        {
            foreach (var snapshot in snapshots)
            {
                if (snapshot.Message is { } forwarded)
                {
                    yield return forwarded;
                }
            }
        }
    }

    /// <summary>The main image of a link preview: its image, or for image links its thumbnail. Video and GIF
    /// previews are skipped, since their thumbnail is only a still frame.</summary>
    private static IEnumerable<ImageSource> GetPreviewImage(IEmbed embed)
    {
        if (embed.Type is EmbedType.Video or EmbedType.Gifv)
        {
            yield break;
        }

        var (url, proxyUrl) = embed.Image is { } image ? (image.Url, image.ProxyUrl)
            : embed.Thumbnail is { } thumbnail ? (thumbnail.Url, thumbnail.ProxyUrl)
            : (null, null);
        if (proxyUrl is not null && IsDiscordProxyUrl(proxyUrl))
        {
            yield return new ImageSource(DescribeOrigin(url), proxyUrl, null, null, null, false, ProxyUrl: proxyUrl);
        }
    }

    /// <summary>"host/file" of the original image URL, e.g. "pbs.twimg.com/G3abc.jpg".</summary>
    private static string DescribeOrigin(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Link preview";
        }

        var file = uri.Segments.Length > 1 ? Uri.UnescapeDataString(uri.Segments[^1]) : "";
        return file.Length > 0 ? $"{uri.Host}/{file}" : uri.Host;
    }

    private static string GetExtension(string name) => Path.GetExtension(name).ToLowerInvariant();
}
