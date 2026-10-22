using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>Spots scam posts from their metadata and links alone, on the gateway thread, and queues the clean-up.
/// Deciding here rather than in the queue means the author is flagged before their next copy arrives, so a burst
/// produces one alert, not one per channel. Other posts that might be scams are queued for comparison with known scam
/// images and, if they look suspicious, for reading the text in their images. Link previews are checked the same way
/// when Discord adds them.</summary>
public sealed class ScamDetector(
    BurstTracker tracker,
    KnownScams known,
    ScamQueue queue,
    IOptionsMonitor<BotOptions> options,
    ILogger<ScamDetector> logger) : IMessageModule
{
    // "Little text": scam posts are mostly screenshots, sometimes with a short line like "just won big on this site".
    private const int ShapeMaxTextLength = 200;

    // Messages whose link previews were already checked, oldest first.
    private const int MaxPreviewsSeen = 1000;
    private readonly HashSet<ulong> _previewsSeen = [];
    private readonly Queue<ulong> _previewsOrder = new();

    /// <summary>Several images and little text, like the typical scam post.</summary>
    public static bool HasScamShape(int imageCount, string text, ScamOptions settings) =>
        imageCount >= settings.ShapeMinImages && text.Length <= ShapeMaxTextLength;

    public string Name => nameof(BotOptions.Scams);

    public string DisplayName => "scam detection";

    public void OnMessage(SocketUserMessage message, SocketGuildChannel channel, IReadOnlyList<ImageSource> images)
    {
        // Only posts with images or links are looked at; most messages stop here.
        var content = ScamContent.From(message, images);
        if (images.Count == 0 && content.Domains.Count == 0)
        {
            return;
        }

        // Webhooks can't be timed out and aren't member accounts; bots and moderators are exempt.
        var settings = options.CurrentValue.Scams;
        if (message.Author is not SocketGuildUser author || author.IsWebhook || ScamRules.IsExempt(author.Guild, author, settings))
        {
            return;
        }

        var post = new PostRef(channel.Id, message.Id);
        var burst = tracker.Record(author.Id, post, images, content.Domains.Count > 0 ? content.Text : null);

        if (burst.FlaggedEventId is { } flaggedEventId)
        {
            Enqueue(new ScamFollowUp(author.Id, post, flaggedEventId));
            return;
        }

        var signals = new ScamSignals(
            burst.Channels,
            burst.Repeated,
            Shape: HasScamShape(images.Count, content.Text, settings),
            images.Count,
            DomainMatches: known.CountDomainMatches(content.Domains));
        var verdict = ScamRules.Decide(signals, settings);
        if (verdict == ScamVerdict.Scam)
        {
            // Another copy can win the race to flag the author; then this one is just more of the same.
            var eventId = tracker.Flag(author.Id, message.Id);
            Enqueue(eventId == message.Id
                ? new ScamDetection(author.Id, post, eventId, message.Timestamp, content, burst.Posts, signals)
                : new ScamFollowUp(author.Id, post, eventId));
        }
        else if (verdict == ScamVerdict.Suspicious || (images.Count > 0 && known.ImageCount > 0) || ScamRules.ShouldRead(signals, settings))
        {
            Enqueue(new ScamCandidate(author.Id, post, message.Timestamp, content, burst.Posts, signals));
        }
    }

    public void OnLinkPreviews(SocketUserMessage message, SocketGuildChannel channel, IReadOnlyList<ImageSource> previews)
    {
        // Discord can update a message more than once; its previews are checked the first time.
        lock (_previewsSeen)
        {
            if (!_previewsSeen.Add(message.Id))
            {
                return;
            }
            _previewsOrder.Enqueue(message.Id);
            if (_previewsOrder.Count > MaxPreviewsSeen)
            {
                _previewsSeen.Remove(_previewsOrder.Dequeue());
            }
        }

        var settings = options.CurrentValue.Scams;
        if (message.Author is not SocketGuildUser author || author.IsWebhook || ScamRules.IsExempt(author.Guild, author, settings))
        {
            return;
        }

        // The post was recorded when it arrived. If its author is flagged, it was deleted as a follow-up already.
        var content = ScamContent.From(message, previews);
        var (channels, repeated, flagged) = tracker.Inspect(author.Id, [], content.Domains.Count > 0 ? content.Text : null);
        if (flagged)
        {
            return;
        }

        var signals = new ScamSignals(Math.Max(1, channels), repeated, Shape: false, previews.Count,
            DomainMatches: known.CountDomainMatches(content.Domains));
        if (known.ImageCount > 0 || ScamRules.ShouldRead(signals, settings))
        {
            var post = new PostRef(channel.Id, message.Id);
            Enqueue(new ScamCandidate(author.Id, post, message.Timestamp, content, [post], signals, LinkPreview: true));
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
