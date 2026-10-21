using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.ScamImages;

/// <summary>Spots scam posts from their metadata alone, on the gateway thread, and queues the clean-up. Deciding here
/// rather than in the queue means the author is flagged before their next copy arrives, so a burst produces one
/// alert, not one per channel.</summary>
public sealed class ScamDetector(
    BurstTracker tracker,
    ScamQueue queue,
    IOptionsMonitor<BotOptions> options,
    ILogger<ScamDetector> logger) : IMessageModule
{
    // "Little text": scam posts are mostly screenshots, sometimes with a short line like "just won big on this site".
    private const int ShapeMaxTextLength = 200;

    public void OnMessage(SocketUserMessage message, SocketGuildChannel channel, IReadOnlyList<ImageSource> images)
    {
        // Webhooks and bots can't be timed out, and their posts aren't from a hijacked member account.
        if (message.Author is not SocketGuildUser author || author.IsBot || author.IsWebhook || IsExempt(author))
        {
            return;
        }

        var settings = options.CurrentValue.ScamImages;
        var post = new PostRef(channel.Id, message.Id);
        var burst = tracker.Record(author.Id, post, images);

        if (burst.FlaggedEventId is { } flaggedEventId)
        {
            Enqueue(new ScamFollowUp(author.Id, post, flaggedEventId));
            return;
        }

        var signals = new ScamSignals(
            burst.Channels,
            Shape: images.Count >= settings.ShapeMinImages && message.Content.Length <= ShapeMaxTextLength,
            images.Count);
        if (!IsScam(signals, settings))
        {
            return;
        }

        // Another copy can win the race to flag the author; then this one is just more of the same.
        var eventId = tracker.Flag(author.Id, message.Id);
        Enqueue(eventId == message.Id
            ? new ScamDetection(author.Id, post, eventId, message.Timestamp, images, burst.Posts, signals)
            : new ScamFollowUp(author.Id, post, eventId));
    }

    /// <summary>The same images in enough channels, or in two channels when the post also looks like a scam post.</summary>
    public static bool IsScam(ScamSignals signals, ScamImageOptions settings) =>
        signals.Channels >= settings.BurstChannels || (signals.Channels >= 2 && signals.Shape);

    /// <summary>Moderators, the owner and exempt roles are never acted on.</summary>
    private bool IsExempt(SocketGuildUser author) =>
        author.Id == author.Guild.OwnerId
        || author.GuildPermissions.Administrator
        || author.GuildPermissions.ManageMessages
        || author.GuildPermissions.ModerateMembers
        || author.Roles.Any(r => options.CurrentValue.ScamImages.ExemptRoleIds.Contains(r.Id));

    private void Enqueue(ScamJob job)
    {
        if (!queue.TryEnqueue(job))
        {
            logger.LogWarning("Scam queue is full; skipped message {MessageId} in {ChannelId}.", job.Post.MessageId, job.Post.ChannelId);
        }
    }
}
