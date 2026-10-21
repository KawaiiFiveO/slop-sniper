using System.Text;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using SlopSniper.Options;
using SlopSniper.Services;
using SlopSniper.State;

namespace SlopSniper.Modules;

// Manage Messages rather than Manage Server, so moderators without server admin rights can use it. Discord applies
// default permissions to the whole group, so reload is limited separately.
[Group("sniper", "slop-sniper controls.")]
[DefaultMemberPermissions(GuildPermission.ManageMessages)]
[CommandContextType(InteractionContextType.Guild)]
public sealed class SniperCommands(
    ScanQueue queue,
    ScanStats stats,
    OpenAiStatus openAi,
    StateStore state,
    Reporter reporter,
    ConfigReloader reloader,
    ILogger<SniperCommands> logger) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("status", "Show scanning status and counters.")]
    public async Task Status()
    {
        var paused = await state.ReadAsync(s => s.ScanningPaused);
        var reply = new StringBuilder()
            .Append("Scanning: ").Append(paused ? "paused" : "on").Append('\n')
            .Append("OpenAI: ").Append(FormatOpenAiStatus()).Append('\n')
            .Append("Queue: ").Append(queue.Count).Append(" waiting\n")
            .Append("Last OpenAI check: ").Append(openAi.LastSuccess is { } last ? Timestamp(last) : "none yet").Append('\n')
            .Append("\nSince ").Append(Timestamp(stats.StartedAt)).Append(":\n")
            .Append("- ").Append(stats.MessagesQueued).Append(" messages scanned, ").Append(stats.MessagesDropped).Append(" dropped (queue full)\n")
            .Append("- ").Append(stats.ImagesChecked).Append(" images checked (").Append(stats.CacheHits).Append(" from cache), ")
            .Append(stats.ImagesDetected).Append(" detected\n")
            .Append("- ").Append(stats.ImagesSkipped).Append(" skipped (too large or unsupported), ")
            .Append(stats.ImagesFailed).Append(" failed");

        await RespondAsync(reply.ToString(), ephemeral: true);
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

    // Owner only: config.json lives on the server, and replies can include server file paths.
    [RequireOwner]
    [SlashCommand("reload", "Re-read config.json without restarting.")]
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

    private string FormatOpenAiStatus()
    {
        if (openAi.SuspendedReason is not { } reason)
        {
            return "ok";
        }

        var retry = openAi.RetryAt is { } retryAt ? $", retrying {Timestamp(retryAt)}" : ", until `/sniper reload` or a restart";
        return $"suspended{retry}\n> {reason}";
    }

    private static string Timestamp(DateTimeOffset time) => TimestampTag.FormatFromDateTimeOffset(time, TimestampTagStyles.Relative);
}
