using Discord;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>What the author posted more than once within the burst window.</summary>
[Flags]
public enum Repeated
{
    None = 0,
    Images = 1,

    /// <summary>The same text, in a message with a link. Plain text isn't tracked.</summary>
    LinkText = 2,
}

/// <summary>Why a post was taken for a scam.</summary>
/// <param name="Channels">Distinct channels the same images or link text were posted to within the burst window.</param>
/// <param name="Shape">Several images and little text, like the typical scam post.</param>
/// <param name="HashMatches">Images that look like a known scam image.</param>
/// <param name="DomainMatches">Links to a known scam domain.</param>
/// <param name="ReportedBy">Mention of the moderator who used "Report as scam".</param>
public sealed record ScamSignals(
    int Channels,
    Repeated Repeated,
    bool Shape,
    int ImageCount,
    int HashMatches = 0,
    int DomainMatches = 0,
    string? ReportedBy = null);

/// <summary>What a post contains that the scam checks look at.</summary>
/// <param name="Text">Including forwarded messages. Shown in alerts, never logged.</param>
/// <param name="Domains">Of the links in <paramref name="Text"/>, normalized.</param>
public sealed record ScamContent(string Text, IReadOnlyList<ImageSource> Images, IReadOnlyList<string> Domains)
{
    public static readonly ScamContent Empty = new("", [], []);

    /// <summary>Attachments only, like the automatic scan: link previews aren't scanned.</summary>
    public static ScamContent From(IMessage message, IReadOnlyList<ImageSource>? images = null)
    {
        var text = ScamLinks.GetText(message);
        return new(text, images ?? ImageFormats.GetImages(message, includeLinkPreviews: false), ScamLinks.GetDomains(text));
    }
}

/// <summary>Work for <see cref="ScamQueue"/>.</summary>
public abstract record ScamJob(ulong AuthorId, PostRef Post);

/// <summary>A post to act on: delete every copy, time out the author and alert the moderators. Also used for a
/// "possible scam" alert, which only alerts.</summary>
/// <param name="EventId">Identifies the detection in the alert's buttons and in what was learned from it: the ID of
/// the post that triggered it.</param>
/// <param name="Posts">Every copy to delete, including <see cref="ScamJob.Post"/>.</param>
/// <param name="Hashes">The images' perceptual hashes, if already computed.</param>
public sealed record ScamDetection(
    ulong AuthorId,
    PostRef Post,
    ulong EventId,
    DateTimeOffset Timestamp,
    ScamContent Content,
    IReadOnlyList<PostRef> Posts,
    ScamSignals Signals,
    IReadOnlyList<ulong>? Hashes = null) : ScamJob(AuthorId, Post);

/// <summary>A post that isn't a scam by its metadata and links alone, to compare against known scam images, or to
/// ask the moderators about.</summary>
public sealed record ScamCandidate(
    ulong AuthorId,
    PostRef Post,
    DateTimeOffset Timestamp,
    ScamContent Content,
    IReadOnlyList<PostRef> Posts,
    ScamSignals Signals) : ScamJob(AuthorId, Post);

/// <summary>Another post with images or links by an author who was already acted on: delete it without a new
/// alert.</summary>
public sealed record ScamFollowUp(ulong AuthorId, PostRef Post, ulong EventId) : ScamJob(AuthorId, Post);
