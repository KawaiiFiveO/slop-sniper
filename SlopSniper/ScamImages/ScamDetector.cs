using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.ScamImages;

/// <summary>Spots scam posts from their metadata alone, on the gateway thread, and queues the clean-up. Deciding here
/// rather than in the queue means the author is flagged before their next copy arrives, so a burst produces one
/// alert, not one per channel. Other image posts are queued for comparison with known scam images.</summary>
public sealed class ScamDetector(
    BurstTracker tracker,
    ScamHashStore known,
    ScamQueue queue,
    IOptionsMonitor<BotOptions> options,
    ILogger<ScamDetector> logger) : IMessageModule
{
    // "Little text": scam posts are mostly screenshots, sometimes with a short line like "just won big on this site".
    private const int ShapeMaxTextLength = 200;

    /// <summary>Several images and little text, like the typical scam post.</summary>
    public static bool HasScamShape(int imageCount, string text, ScamImageOptions settings) =>
        imageCount >= settings.ShapeMinImages && text.Length <= ShapeMaxTextLength;

    public string Name => nameof(BotOptions.ScamImages);

    public string DisplayName => "scam detection";

    public void OnMessage(SocketUserMessage message, SocketGuildChannel channel, IReadOnlyList<ImageSource> images)
    {
        // Webhooks can't be timed out and aren't member accounts; bots and moderators are exempt.
        var settings = options.CurrentValue.ScamImages;
        if (message.Author is not SocketGuildUser author || author.IsWebhook || ScamRules.IsExempt(author.Guild, author, settings))
        {
            return;
        }

        var post = new PostRef(channel.Id, message.Id);
        var burst = tracker.Record(author.Id, post, images);

        if (burst.FlaggedEventId is { } flaggedEventId)
        {
            Enqueue(new ScamFollowUp(author.Id, post, flaggedEventId));
            return;
        }

        var signals = new ScamSignals(
            burst.Channels,
            Shape: HasScamShape(images.Count, message.Content, settings),
            images.Count);
        if (ScamRules.Decide(signals, settings) == ScamVerdict.Scam)
        {
            // Another copy can win the race to flag the author; then this one is just more of the same.
            var eventId = tracker.Flag(author.Id, message.Id);
            Enqueue(eventId == message.Id
                ? new ScamDetection(author.Id, post, eventId, message.Timestamp, images, burst.Posts, signals)
                : new ScamFollowUp(author.Id, post, eventId));
        }
        else if (known.Count > 0)
        {
            Enqueue(new ScamCandidate(author.Id, post, message.Timestamp, images, burst.Posts, signals));
        }
    }

    private void Enqueue(ScamJob job)
    {
        if (!queue.TryEnqueue(job))
        {
            logger.LogWarning("Scam queue is full; skipped message {MessageId} in {ChannelId}.", job.Post.MessageId, job.Post.ChannelId);
        }
    }
}
