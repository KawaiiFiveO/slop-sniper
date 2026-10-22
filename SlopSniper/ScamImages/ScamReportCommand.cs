using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Modules;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.ScamImages;

/// <summary>Right-click a message > Apps > Report as scam. Moderators teach the bot a scam it missed: its images are
/// remembered, and the post is deleted and its author timed out like an automatic detection. GuildId only.</summary>
public sealed class ScamReportCommand(
    ScamModerator moderator,
    ScamResponder responder,
    ScamImageHasher hasher,
    ScamHashStore known,
    BurstTracker tracker,
    IOptionsMonitor<BotOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<ScamReportCommand> logger) : InteractionModuleBase<SocketInteractionContext>
{
    [DefaultMemberPermissions(GuildPermission.ModerateMembers)]
    [CommandContextType(InteractionContextType.Guild)]
    [RequirePermission(GuildPermission.ModerateMembers)]
    [MessageCommand("Report as scam")]
    public async Task Report(IMessage message)
    {
        var images = ImageFormats.GetImages(message, includeLinkPreviews: false);
        if (images.Count == 0)
        {
            await RespondAsync("That message has no images.", ephemeral: true);
            return;
        }

        // Downloading and moderating can take longer than the 3 seconds Discord allows for a reply.
        await DeferAsync(ephemeral: true);
        logger.LogInformation("Message {MessageId} by {UserId} reported as scam by {Moderator} ({ModeratorId}).",
            message.Id, message.Author.Id, Context.User.Username, Context.User.Id);

        // Moderators, bots and webhooks are never acted on, but their images can still be learned.
        var member = message.Author.IsWebhook ? null : await moderator.GetMemberAsync(message.Author.Id);
        if (message.Author.IsWebhook || (member is not null && ScamRules.IsExempt(Context.Guild, member, options.CurrentValue.ScamImages)))
        {
            var hashes = await hasher.HashAsync(images, lifetime.ApplicationStopping);
            var learned = await known.AddAsync(hashes, message.Id, ScamImageOrigin.Reported);
            await FollowupAsync($"Learned {learned} new scam images. The author is a moderator, bot, webhook or has an exempt role, so nothing else was done.",
                ephemeral: true);
            return;
        }

        var post = new PostRef(Context.Interaction.ChannelId ?? message.Channel.Id, message.Id);
        var detection = new ScamDetection(message.Author.Id, post, message.Id, message.Timestamp, images, [post],
            new ScamSignals(1, false, images.Count, ReportedBy: Context.User.Mention));

        tracker.Flag(message.Author.Id, message.Id);
        var outcome = await responder.ActAsync(detection, ScamImageOrigin.Reported, alert: true, lifetime.ApplicationStopping,
            Context.User.Username);
        var actions = outcome.Actions;
        var timeout = actions.TimedOutFor is not null ? "timed out the author"
            : actions.TimeoutError is { } error ? $"couldn't time out the author ({error})"
            : "didn't time out the author";
        await FollowupAsync($"Deleted {actions.Deleted} of {actions.ToDelete} posts, {timeout}, and learned {outcome.Learned} new scam images. Details are in the log channel.",
            ephemeral: true);
    }
}
