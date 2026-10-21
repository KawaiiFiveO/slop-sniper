using Discord;

namespace SlopSniper.Services;

/// <summary>Decides which attachments are images, and which of those the API accepts.</summary>
public static class ImageFormats
{
    private static readonly HashSet<string> SupportedTypes = ["image/png", "image/jpeg", "image/webp"];
    private static readonly HashSet<string> SupportedExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    // Image formats the API doesn't accept. Listed so they're reported as unsupported instead of being ignored.
    private static readonly HashSet<string> OtherImageExtensions = [".gif", ".avif", ".heic", ".heif", ".bmp", ".tif", ".tiff"];

    /// <summary>Image attachments of the message, including those in forwarded messages.</summary>
    public static IReadOnlyList<IAttachment> GetImages(IMessage message)
    {
        var forwarded = message is IUserMessage { ForwardedMessages: { } snapshots }
            ? snapshots.SelectMany(s => s.Message?.Attachments ?? [])
            : [];
        return message.Attachments.Concat(forwarded).Where(IsImage).ToList();
    }

    public static bool IsImage(IAttachment attachment) =>
        attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
        || SupportedExtensions.Contains(GetExtension(attachment))
        || OtherImageExtensions.Contains(GetExtension(attachment));

    /// <summary>Based on Discord's metadata, before downloading. The file itself is checked by <see cref="Sniff"/>.</summary>
    public static bool IsSupported(IAttachment attachment) =>
        attachment.ContentType is { } contentType
            ? SupportedTypes.Contains(contentType.Split(';')[0].Trim().ToLowerInvariant())
            : SupportedExtensions.Contains(GetExtension(attachment));

    /// <summary>Describes an attachment's type for "unsupported type" messages.</summary>
    public static string Describe(IAttachment attachment) =>
        attachment.ContentType ?? (GetExtension(attachment) is { Length: > 0 } extension ? extension : "unknown type");

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

    private static string GetExtension(IAttachment attachment) =>
        Path.GetExtension(attachment.Filename).ToLowerInvariant();
}
