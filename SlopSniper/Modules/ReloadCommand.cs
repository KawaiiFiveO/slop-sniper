using System.Text;
using Discord;
using Discord.Interactions;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Modules;

/// <summary>/sniper-reload. Global and installable on user accounts, so the owner can reload from anywhere, including
/// in user-app-only mode (GuildId 0), where /sniper doesn't exist. Owner only: config.json lives on the server, and
/// replies can include server file paths.</summary>
public sealed class ReloadCommand(ConfigReloader reloader) : InteractionModuleBase<SocketInteractionContext>
{
    // No default member permissions: they could hide the command from the owner, and user installs may not follow
    // them anyway. Others see it but are refused.
    [IntegrationType(ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall)]
    [CommandContextType(InteractionContextType.Guild, InteractionContextType.BotDm, InteractionContextType.PrivateChannel)]
    [RequireBotOwner]
    [SlashCommand("sniper-reload", "Re-read slop-sniper's config.json without restarting.")]
    public async Task Reload()
    {
        var result = reloader.Reload();
        var reply = new StringBuilder();

        if (result.Errors.Count > 0)
        {
            reply.Append("config.json was not reloaded:\n");
            foreach (var error in result.Errors)
            {
                reply.Append("- ").Append(error).Append('\n');
            }
        }
        else if (result.Changed.Count == 0)
        {
            reply.Append($"No changes found in {Path.Combine(AppContext.BaseDirectory, BotOptions.FileName)}.");
        }
        else
        {
            reply.Append($"config.json reloaded. Changed: {string.Join(", ", result.Changed)}.");
            if (result.RestartRequired.Count > 0)
            {
                reply.Append($" {string.Join(" and ", result.RestartRequired)} changed, which needs a restart to take effect.");
            }
        }

        var text = reply.ToString();
        if (text.Length > DiscordConfig.MaxMessageSize)
        {
            text = text[..(DiscordConfig.MaxMessageSize - 1)] + "…";
        }

        await RespondAsync(text, ephemeral: true);
    }
}
