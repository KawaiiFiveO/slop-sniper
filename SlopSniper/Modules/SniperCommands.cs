using System.Text;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using SlopSniper.Options;
using SlopSniper.Services;
using SlopSniper.State;

namespace SlopSniper.Modules;

// Discord applies default permissions to the whole group, so it shows /sniper to moderators; pausing is limited to
// admins below. Every permission is also checked here, so a broad override in Server Settings > Integrations can't
// hand these to anyone else. Reload is a separate owner-only command (ReloadCommand).
[Group("sniper", "slop-sniper controls.")]
[DefaultMemberPermissions(GuildPermission.ManageMessages)]
[CommandContextType(InteractionContextType.Guild)]
[RequirePermission(GuildPermission.ManageMessages)]
public sealed class SniperCommands(
    IEnumerable<IMessageModule> modules,
    IEnumerable<IStatusSection> sections,
    StateStore state,
    Reporter reporter,
    ILogger<SniperCommands> logger) : InteractionModuleBase<SocketInteractionContext>
{
    private const string All = "all";

    private readonly IMessageModule[] _modules = modules.ToArray();

    [SlashCommand("status", "Show scanning status and counters.")]
    public async Task Status()
    {
        var paused = await state.ReadAsync(s => s.PausedModules.ToArray());
        var reply = new StringBuilder("Automatic scanning: ")
            .AppendJoin(" · ", _modules.Select(m => $"{m.DisplayName} {(paused.Contains(m.Name) ? "**paused**" : "on")}"))
            .Append('\n');

        foreach (var section in sections)
        {
            reply.Append('\n');
            section.AppendStatus(reply);
        }

        await RespondAsync(reply.ToString().TrimEnd(), ephemeral: true);
    }

    [RequirePermission(GuildPermission.ManageGuild)]
    [SlashCommand("pause", "Stop automatic scanning, of everything or one module. Manual checks still work.")]
    public Task Pause(
        [Summary(description: "What to pause. Default: everything.")]
        [Choice("everything", All), Choice("provenance checks (OpenAI)", nameof(BotOptions.Provenance)), Choice("scam detection", nameof(BotOptions.ScamImages))]
        string module = All) =>
        SetPausedAsync(module, paused: true);

    [RequirePermission(GuildPermission.ManageGuild)]
    [SlashCommand("resume", "Resume automatic scanning, of everything or one module.")]
    public Task Resume(
        [Summary(description: "What to resume. Default: everything.")]
        [Choice("everything", All), Choice("provenance checks (OpenAI)", nameof(BotOptions.Provenance)), Choice("scam detection", nameof(BotOptions.ScamImages))]
        string module = All) =>
        SetPausedAsync(module, paused: false);

    private async Task SetPausedAsync(string target, bool paused)
    {
        var targets = target == All ? _modules : _modules.Where(m => m.Name == target).ToArray();
        if (targets.Length == 0)
        {
            await RespondAsync("That module is turned off in the bot's config.", ephemeral: true);
            return;
        }

        List<string> changed = [];
        await state.UpdateAsync(s =>
        {
            foreach (var module in targets)
            {
                if (paused ? !s.PausedModules.Contains(module.Name) : s.PausedModules.Contains(module.Name))
                {
                    if (paused)
                    {
                        s.PausedModules.Add(module.Name);
                    }
                    else
                    {
                        s.PausedModules.Remove(module.Name);
                    }
                    changed.Add(module.DisplayName);
                }
            }
        });

        var verb = paused ? "paused" : "resumed";
        if (changed.Count == 0)
        {
            await RespondAsync($"Nothing to change: already {(paused ? "paused" : "on")}.", ephemeral: true);
            return;
        }

        var what = string.Join(" and ", changed);
        logger.LogInformation("Automatic scanning {Verb} for {Modules} by {User} ({UserId}).", verb, what, Context.User.Username, Context.User.Id);
        await RespondAsync($"Automatic scanning {verb}: {what}.", ephemeral: true);
        await reporter.SendAlertAsync($"Automatic scanning {verb} for {what} by {Context.User.Mention}.");
    }
}
