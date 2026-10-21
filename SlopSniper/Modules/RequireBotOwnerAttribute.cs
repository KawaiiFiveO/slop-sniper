using Discord;
using Discord.Interactions;

namespace SlopSniper.Modules;

/// <summary>Only the application owner, or any member of the team that owns it. Unlike Discord.Net's RequireOwner,
/// this also covers team-owned applications.</summary>
public sealed class RequireBotOwnerAttribute : PreconditionAttribute
{
    public override async Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services) =>
        await IsOwnerAsync(context)
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError("Only the bot owner can use this command.");

    public static async Task<bool> IsOwnerAsync(IInteractionContext context)
    {
        // Discord.Net caches the application info after the first request.
        var application = await context.Client.GetApplicationInfoAsync();
        return application.Team is { } team
            ? team.TeamMembers.Any(m => m.User.Id == context.User.Id)
            : application.Owner.Id == context.User.Id;
    }
}
