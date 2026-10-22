using SlopSniper.Services;

namespace SlopSniper.ScamImages;

/// <summary>Why a post was taken for a scam.</summary>
/// <param name="Channels">Distinct channels the same images were posted to within the burst window.</param>
/// <param name="Shape">Several images and little text, like the typical scam post.</param>
/// <param name="HashMatches">Images that look like a known scam image.</param>
/// <param name="ReportedBy">Mention of the moderator who used "Report as scam".</param>
public sealed record ScamSignals(int Channels, bool Shape, int ImageCount, int HashMatches = 0, string? ReportedBy = null);

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
    IReadOnlyList<ImageSource> Images,
    IReadOnlyList<PostRef> Posts,
    ScamSignals Signals,
    IReadOnlyList<ulong>? Hashes = null) : ScamJob(AuthorId, Post);

/// <summary>A post that isn't a scam by its metadata alone, to compare against known scam images.</summary>
public sealed record ScamCandidate(
    ulong AuthorId,
    PostRef Post,
    DateTimeOffset Timestamp,
    IReadOnlyList<ImageSource> Images,
    IReadOnlyList<PostRef> Posts,
    ScamSignals Signals) : ScamJob(AuthorId, Post);

/// <summary>Another image post by an author who was already acted on: delete it without a new alert.</summary>
public sealed record ScamFollowUp(ulong AuthorId, PostRef Post, ulong EventId) : ScamJob(AuthorId, Post);
