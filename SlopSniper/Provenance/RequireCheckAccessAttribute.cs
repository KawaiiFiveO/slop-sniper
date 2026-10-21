using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SlopSniper.Modules;
using SlopSniper.Options;

namespace SlopSniper.Provenance;

/// <summary>Who can run a manual check. The command is installable on user accounts and usable anywhere, so this is
/// enforced here rather than with Discord's default member permissions, which a user-installed command may not
/// follow outside the home server.</summary>
public sealed class RequireCheckAccessAttribute : PreconditionAttribute
{
    public override async Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
    {
        var homeGuildId = services.GetRequiredService<IOptions<BotOptions>>().Value.GuildId;
        var allowed = services.GetRequiredService<IOptionsMonitor<BotOptions>>().CurrentValue.Provenance.UserAppUserIds;

        // Moderators of the home server, where the bot scans automatically.
        if (homeGuildId != 0
            && context.Interaction.GuildId == homeGuildId
            && context.User is IGuildUser { GuildPermissions.ManageMessages: true })
        {
            return PreconditionResult.FromSuccess();
        }

        if (allowed.Contains(context.User.Id) || await RequireBotOwnerAttribute.IsOwnerAsync(context))
        {
            return PreconditionResult.FromSuccess();
        }

        return PreconditionResult.FromError("You don't have access to this command.");
    }
}
