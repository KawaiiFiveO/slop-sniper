using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Modules;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>Right-click a message > Apps > Report as scam. Moderators teach the bot a scam it missed: its images and
/// link domains are remembered, and the post is deleted and its author timed out like an automatic detection. Works on
/// any message, with or without images or links. GuildId only.</summary>
public sealed class ScamReportCommand(
    ScamModerator moderator,
    ScamResponder responder,
    ScamImageHasher hasher,
    KnownScams known,
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
        // Downloading and moderating can take longer than the 3 seconds Discord allows for a reply.
        await DeferAsync(ephemeral: true);
        logger.LogInformation("Message {MessageId} by {UserId} reported as scam by {Moderator} ({ModeratorId}).",
            message.Id, message.Author.Id, Context.User.Username, Context.User.Id);

        // Moderators, bots and webhooks are never acted on, but their images and domains can still be learned.
        var content = ScamContent.From(message);
        var member = message.Author.IsWebhook ? null : await moderator.GetMemberAsync(message.Author.Id);
        if (message.Author.IsWebhook || (member is not null && ScamRules.IsExempt(Context.Guild, member, options.CurrentValue.Scams)))
        {
            var hashes = await hasher.HashAsync(content.Images, lifetime.ApplicationStopping);
            var learned = await known.AddAsync(hashes, content.Domains, message.Id, ScamOrigin.Reported);
            await FollowupAsync($"Learned {ScamReportFormatter.DescribeLearned(learned)}. The author is a moderator, bot, webhook or has an exempt role, so nothing else was done.",
                ephemeral: true);
            return;
        }

        var post = new PostRef(Context.Interaction.ChannelId ?? message.Channel.Id, message.Id);
        var detection = new ScamDetection(message.Author.Id, post, message.Id, message.Timestamp, content, [post],
            new ScamSignals(1, Repeated.None, false, content.Images.Count, ReportedBy: Context.User.Mention));

        tracker.Flag(message.Author.Id, message.Id);
        var outcome = await responder.ActAsync(detection, ScamOrigin.Reported, alert: true, lifetime.ApplicationStopping,
            Context.User.Username);
        var actions = outcome.Actions;
        var timeout = actions.TimedOutFor is not null ? "timed out the author"
            : actions.TimeoutError is { } error ? $"couldn't time out the author ({error})"
            : actions.PreviousTimeout is not null ? "left the author's longer timeout in place"
            : "didn't time out the author";
        await FollowupAsync($"Deleted {actions.Deleted} of {actions.ToDelete} posts, {timeout}, and learned {ScamReportFormatter.DescribeLearned(outcome.Learned)}. Details are in the log channel.",
            ephemeral: true);
    }
}
