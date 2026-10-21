using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace SlopSniper.ScamImages;

/// <summary>The buttons on scam alerts. Everything they need is in the button's ID, so they keep working after a
/// restart.</summary>
public sealed class ScamButtons(
    ScamModerator moderator,
    BurstTracker tracker,
    ScamStats stats,
    ILogger<ScamButtons> logger) : InteractionModuleBase<SocketInteractionContext>
{
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [ComponentInteraction(ScamReportFormatter.UndoButtonPrefix + ":*:*")]
    public async Task Undo(ulong eventId, ulong authorId)
    {
        // Acknowledges the click; the alert is edited below.
        await DeferAsync();

        var timeoutError = await moderator.RemoveTimeoutAsync(authorId, Context.User.Username);
        tracker.Unflag(authorId);
        stats.Undo();
        logger.LogInformation("Scam event {EventId} for user {UserId} undone by {Moderator} ({ModeratorId}).",
            eventId, authorId, Context.User.Username, Context.User.Id);

        var alert = ((SocketMessageComponent)Context.Interaction).Message.Embeds.FirstOrDefault();
        await ModifyOriginalResponseAsync(m =>
        {
            if (alert is not null)
            {
                m.Embeds = new[] { ScamReportFormatter.MarkUndone(alert, Context.User.Mention, timeoutError) };
            }
            m.Components = new ComponentBuilder().Build();
        });
    }
}
