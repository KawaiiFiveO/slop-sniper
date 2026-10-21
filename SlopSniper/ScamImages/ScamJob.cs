using SlopSniper.Services;

namespace SlopSniper.ScamImages;

/// <summary>Why a post was taken for a scam.</summary>
/// <param name="Channels">Distinct channels the same images were posted to within the burst window.</param>
/// <param name="Shape">Several images and little text, like the typical scam post.</param>
public sealed record ScamSignals(int Channels, bool Shape, int ImageCount);

/// <summary>Work for <see cref="ScamQueue"/>.</summary>
public abstract record ScamJob(ulong AuthorId, PostRef Post);

/// <summary>A new detection: act on every copy of the post and alert the moderators.</summary>
/// <param name="EventId">Identifies the detection in the alert's buttons: the ID of the post that triggered it.</param>
/// <param name="Posts">Every copy to delete, including <see cref="ScamJob.Post"/>.</param>
public sealed record ScamDetection(
    ulong AuthorId,
    PostRef Post,
    ulong EventId,
    DateTimeOffset Timestamp,
    IReadOnlyList<ImageSource> Images,
    IReadOnlyList<PostRef> Posts,
    ScamSignals Signals) : ScamJob(AuthorId, Post);

/// <summary>Another image post by an author who was already acted on: delete it without a new alert.</summary>
public sealed record ScamFollowUp(ulong AuthorId, PostRef Post, ulong EventId) : ScamJob(AuthorId, Post);
