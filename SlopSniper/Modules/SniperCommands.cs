using System.Text;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using SlopSniper.Services;
using SlopSniper.State;

namespace SlopSniper.Modules;

// Manage Messages rather than Manage Server, so moderators without server admin rights can use it. Reload is a
// separate owner-only command (ReloadCommand).
[Group("sniper", "slop-sniper controls.")]
[DefaultMemberPermissions(GuildPermission.ManageMessages)]
[CommandContextType(InteractionContextType.Guild)]
public sealed class SniperCommands(
    IEnumerable<IStatusSection> sections,
    StateStore state,
    Reporter reporter,
    ILogger<SniperCommands> logger) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("status", "Show scanning status and counters.")]
    public async Task Status()
    {
        var paused = await state.ReadAsync(s => s.ScanningPaused);
        var reply = new StringBuilder()
            .Append("Scanning: ").Append(paused ? "paused" : "on").Append('\n');

        foreach (var section in sections)
        {
            reply.Append('\n');
            section.AppendStatus(reply);
        }

        await RespondAsync(reply.ToString().TrimEnd(), ephemeral: true);
    }

    [SlashCommand("pause", "Stop scanning new images automatically. Manual checks still work.")]
    public async Task Pause()
    {
        await SetPausedAsync(true);
    }

    [SlashCommand("resume", "Resume scanning new images automatically.")]
    public async Task Resume()
    {
        await SetPausedAsync(false);
    }

    private async Task SetPausedAsync(bool paused)
    {
        var verb = paused ? "paused" : "resumed";
        if (await state.ReadAsync(s => s.ScanningPaused) == paused)
        {
            await RespondAsync($"Scanning is already {(paused ? "paused" : "on")}.", ephemeral: true);
            return;
        }

        await state.UpdateAsync(s => s.ScanningPaused = paused);
        logger.LogInformation("Scanning {Verb} by {User} ({UserId}).", verb, Context.User.Username, Context.User.Id);
        await RespondAsync($"Automatic scanning {verb}.", ephemeral: true);
        await reporter.SendAlertAsync($"Automatic scanning {verb} by {Context.User.Mention}.");
    }
}
