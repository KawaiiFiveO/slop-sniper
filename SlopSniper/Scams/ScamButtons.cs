using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Modules;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>The buttons on scam alerts. Everything they need is in the button's ID, so they keep working after a
/// restart.</summary>
[RequirePermission(GuildPermission.ModerateMembers)]
public sealed class ScamButtons(
    ScamModerator moderator,
    ScamResponder responder,
    ScamImageHasher hasher,
    KnownScams known,
    BurstTracker tracker,
    ScamStats stats,
    TimeProvider time,
    IOptionsMonitor<BotOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<ScamButtons> logger) : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction(ScamReportFormatter.UndoButtonPrefix + ":*:*")]
    public async Task Undo(ulong eventId, ulong authorId)
    {
        // Acknowledges the click; the alert is edited below.
        await DeferAsync();

        var timeoutError = await moderator.RemoveTimeoutAsync(authorId, Context.User.Username);
        tracker.Unflag(authorId);
        var forgotten = await known.RemoveEventAsync(eventId);
        stats.Undo();
        logger.LogInformation("Scam event {EventId} for user {UserId} undone by {Moderator} ({ModeratorId}); forgot {Images} images and {Domains} domains.",
            eventId, authorId, Context.User.Username, Context.User.Id, forgotten.Images, forgotten.Domains);

        await UpdateAlertAsync(alert => ScamReportFormatter.MarkUndone(alert, Context.User.Mention, timeoutError, forgotten), null);
    }

    [ComponentInteraction(ScamReportFormatter.ConfirmButtonPrefix + ":*:*:*")]
    public async Task Confirm(ulong channelId, ulong messageId, ulong authorId)
    {
        await DeferAsync();

        if (await moderator.GetMemberAsync(authorId) is { } member
            && ScamRules.IsExempt(Context.Guild, member, options.CurrentValue.Scams))
        {
            await FollowupAsync("The author is now a moderator or has an exempt role, so nothing was done.", ephemeral: true);
            return;
        }

        // The post may be gone already; the author is still timed out.
        var post = new PostRef(channelId, messageId);
        var message = await GetMessageAsync(post);
        var content = message is null ? ScamContent.Empty : ScamContent.From(message);
        var detection = new ScamDetection(authorId, post, messageId, message?.Timestamp ?? time.GetUtcNow(), content, [post],
            new ScamSignals(1, Repeated.None, false, content.Images.Count));

        tracker.Flag(authorId, messageId);
        var outcome = await responder.ActAsync(detection, ScamOrigin.Confirmed, alert: false, lifetime.ApplicationStopping,
            Context.User.Username);
        logger.LogInformation("Possible scam {MessageId} by {UserId} confirmed by {Moderator} ({ModeratorId}).",
            messageId, authorId, Context.User.Username, Context.User.Id);

        await UpdateAlertAsync(alert => ScamReportFormatter.MarkConfirmed(alert, Context.User.Mention, outcome),
            ScamReportFormatter.BuildUndoButton(messageId, authorId));
    }

    [ComponentInteraction(ScamReportFormatter.DismissButtonPrefix + ":*:*")]
    public async Task Dismiss(ulong channelId, ulong messageId)
    {
        await DeferAsync();

        // A false positive: forget the known images and domains it matched, so they don't trigger again.
        var message = await GetMessageAsync(new PostRef(channelId, messageId));
        var content = message is null ? ScamContent.Empty : ScamContent.From(message);
        var hashes = await hasher.HashAsync(content.Images, lifetime.ApplicationStopping);
        var forgotten = await known.RemoveMatchingAsync(hashes, content.Domains);
        logger.LogInformation("Possible scam {MessageId} dismissed by {Moderator} ({ModeratorId}); forgot {Images} images and {Domains} domains.",
            messageId, Context.User.Username, Context.User.Id, forgotten.Images, forgotten.Domains);

        await UpdateAlertAsync(alert => ScamReportFormatter.MarkDismissed(alert, Context.User.Mention, forgotten), null);
    }

    /// <param name="components">Null removes the buttons.</param>
    private async Task UpdateAlertAsync(Func<IEmbed, Embed> update, MessageComponent? components)
    {
        var alert = ((SocketMessageComponent)Context.Interaction).Message.Embeds.FirstOrDefault();
        await ModifyOriginalResponseAsync(m =>
        {
            if (alert is not null)
            {
                m.Embeds = new[] { update(alert) };
            }
            m.Components = components ?? new ComponentBuilder().Build();
        });
    }

    private async Task<IMessage?> GetMessageAsync(PostRef post)
    {
        if (Context.Guild.GetChannel(post.ChannelId) is not IMessageChannel channel)
        {
            return null;
        }

        try
        {
            return await channel.GetMessageAsync(post.MessageId);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning("Couldn't fetch message {MessageId}: {Error}", post.MessageId, ex.Message);
            return null;
        }
    }
}
