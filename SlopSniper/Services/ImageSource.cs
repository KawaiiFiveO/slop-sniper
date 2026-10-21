using Discord;

namespace SlopSniper.Services;

/// <summary>An image to check: a message attachment, or a link preview's image fetched through Discord's media proxy.</summary>
/// <param name="Name">The file name, or for a link preview, where the image came from.</param>
/// <param name="AttachmentId">Null for link-preview images.</param>
/// <param name="ContentType">From Discord's metadata, if known. The file itself is checked after downloading.</param>
/// <param name="Size">In bytes, if known before downloading.</param>
/// <param name="ProxyUrl">Discord's media proxy URL, which can serve a resized copy. For link previews it's the same
/// as <paramref name="Url"/>.</param>
/// <param name="Width">In pixels, if known before downloading.</param>
public sealed record ImageSource(
    string Name,
    string Url,
    ulong? AttachmentId,
    string? ContentType,
    long? Size,
    bool IsSpoiler,
    string? ProxyUrl = null,
    int? Width = null,
    int? Height = null)
{
    public static ImageSource FromAttachment(IAttachment attachment) => new(
        attachment.Filename, attachment.Url, attachment.Id, attachment.ContentType, attachment.Size, attachment.IsSpoiler(),
        attachment.ProxyUrl, attachment.Width, attachment.Height);

    /// <summary>For logs, which never include URLs.</summary>
    public string LogName => AttachmentId is { } id ? $"attachment {id}" : "link-preview image";
}
