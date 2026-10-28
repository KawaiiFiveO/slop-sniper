using System.Net;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;

namespace SlopSniper.Scams;

/// <param name="Deleted">Copies deleted, including ones that were already gone.</param>
/// <param name="DeleteError">Why some copies couldn't be deleted, if any couldn't.</param>
/// <param name="TimedOutFor">Null if the author wasn't timed out.</param>
/// <param name="TimeoutError">Why the timeout failed, if it was attempted.</param>
/// <param name="TimedOutUntil">When the bot's timeout ends, so Undo can tell it from one set since.</param>
/// <param name="PreviousTimeout">When a timeout the author already had ends: replaced if it was shorter (Undo puts it
/// back), otherwise left as it was and the author isn't timed out again.</param>
public sealed record ScamActions(
    int Deleted,
    int ToDelete,
    string? DeleteError,
    TimeSpan? TimedOutFor,
    string? TimeoutError,
    DateTimeOffset? TimedOutUntil = null,
    DateTimeOffset? PreviousTimeout = null);

public enum TimeoutUndoResult
{
    /// <summary>The bot hadn't timed the author out, the timeout already ended, or they left.</summary>
    None,
    Lifted,

    /// <summary>The shorter timeout the bot replaced was put back.</summary>
    Restored,

    /// <summary>The timeout was changed after the bot set it, by a moderator or another bot, so it isn't the bot's.</summary>
    LeftInPlace,
    Failed,
}

/// <param name="Until">When the restored or left timeout ends.</param>
public sealed record TimeoutUndo(TimeoutUndoResult Result, DateTimeOffset? Until = null, string? Error = null);

/// <summary>Deletes scam posts and times out their author. Failures are collected for the alert, never thrown. Other
/// bots (anti-spam) may time out the same author, so a longer timeout is never shortened and Undo only removes the
/// bot's own.</summary>
public sealed class ScamModerator(
    DiscordSocketClient client,
    TimeProvider time,
    IOptions<BotOptions> startupOptions,
    IOptionsMonitor<BotOptions> options,
    ILogger<ScamModerator> logger)
{
    private const string FollowUpReason = "slop-sniper: more posts from an account already acted on for scams";

    // A timeout still counts as the bot's if it ends this close to the one the bot set.
    private static readonly TimeSpan SameTimeoutTolerance = TimeSpan.FromSeconds(1);

    /// <param name="auditReason">Shown in Discord's audit log for the deletes and the timeout.</param>
    public async Task<ScamActions> ActAsync(ulong authorId, IReadOnlyList<PostRef> posts, string auditReason)
    {
        var settings = options.CurrentValue.Scams;

        var (deleted, deleteError) = settings.DeleteMessages ? await DeleteAsync(posts, auditReason) : (0, null);

        DateTimeOffset? timedOutUntil = null;
        DateTimeOffset? previous = null;
        string? timeoutError = null;
        if (settings.TimeoutMinutes > 0)
        {
            timeoutError = await RunAsync(async () =>
            {
                var user = await GetFreshMemberAsync(authorId) ?? throw new InvalidOperationException("they already left the server");
                var now = time.GetUtcNow();
                var until = now + TimeSpan.FromMinutes(settings.TimeoutMinutes);
                previous = user.TimedOutUntil > now ? user.TimedOutUntil : null;
                if (previous >= until)
                {
                    return;
                }
                await user.ModifyAsync(u => u.TimedOutUntil = until, new RequestOptions { AuditLogReason = auditReason });
                timedOutUntil = until;
            }, "time out user", authorId);
        }

        return new ScamActions(deleted, settings.DeleteMessages ? posts.Count : 0, deleteError,
            timedOutUntil is null ? null : TimeSpan.FromMinutes(settings.TimeoutMinutes), timeoutError,
            timedOutUntil, timeoutError is null ? previous : null);
    }

    /// <summary>Deletes one more copy from an author who was already acted on.</summary>
    public async Task<bool> DeleteFollowUpAsync(PostRef post)
    {
        if (!options.CurrentValue.Scams.DeleteMessages)
        {
            return false;
        }
        var (deleted, _) = await DeleteAsync([post], FollowUpReason);
        return deleted == 1;
    }

    /// <summary>Undoes the bot's timeout: puts back the shorter one it replaced, or lifts it. Leaves any other timeout,
    /// including one set after the bot's.</summary>
    /// <param name="ours">When the bot's timeout ends, or null if it didn't set one.</param>
    /// <param name="previous">When the timeout it replaced ends, or null.</param>
    public async Task<TimeoutUndo> UndoTimeoutAsync(ulong authorId, DateTimeOffset? ours, DateTimeOffset? previous, string moderator)
    {
        if (ours is null)
        {
            return new TimeoutUndo(TimeoutUndoResult.None);
        }

        var result = new TimeoutUndo(TimeoutUndoResult.None);
        var error = await RunAsync(async () =>
        {
            var now = time.GetUtcNow();
            if (await GetFreshMemberAsync(authorId) is not { TimedOutUntil: { } current } user || current <= now)
            {
                return;
            }
            if ((current - ours.Value).Duration() > SameTimeoutTolerance)
            {
                result = new TimeoutUndo(TimeoutUndoResult.LeftInPlace, current);
                return;
            }

            var restore = previous > now ? previous : null;
            await user.ModifyAsync(u => u.TimedOutUntil = restore, new RequestOptions
            {
                AuditLogReason = $"slop-sniper: scam timeout undone by {moderator}" + (restore is null ? "" : ", earlier timeout restored"),
            });
            result = restore is null ? new TimeoutUndo(TimeoutUndoResult.Lifted) : new TimeoutUndo(TimeoutUndoResult.Restored, restore);
        }, "undo the timeout of user", authorId);
        return error is null ? result : new TimeoutUndo(TimeoutUndoResult.Failed, Error: error);
    }

    /// <summary>For alerts posted before the bot recorded its timeouts: lifts whatever timeout the author has.</summary>
    /// <returns>Null if the timeout was lifted, or why it couldn't be.</returns>
    public Task<string?> RemoveTimeoutAsync(ulong authorId, string moderator) =>
        RunAsync(async () =>
        {
            if (await GetMemberAsync(authorId) is { } user)
            {
                await user.RemoveTimeOutAsync(new RequestOptions { AuditLogReason = $"slop-sniper: scam timeout undone by {moderator}" });
            }
        }, "lift the timeout of user", authorId);

    /// <summary>Warns if the bot can't do what it's configured to.</summary>
    public void CheckPermissions()
    {
        if (client.GetGuild(startupOptions.Value.GuildId)?.CurrentUser?.GuildPermissions is not { } permissions)
        {
            return;
        }

        var settings = options.CurrentValue.Scams;
        if (settings.DeleteMessages && !permissions.ManageMessages)
        {
            logger.LogWarning("Scams: the bot lacks Manage Messages, so it can't delete scam posts.");
        }
        if (settings.TimeoutMinutes > 0 && !permissions.ModerateMembers)
        {
            logger.LogWarning("Scams: the bot lacks Moderate Members, so it can't time out scam posters.");
        }
    }

    private async Task<(int Deleted, string? Error)> DeleteAsync(IReadOnlyList<PostRef> posts, string auditReason)
    {
        var guild = client.GetGuild(startupOptions.Value.GuildId);
        var deleted = 0;
        string? error = null;
        foreach (var post in posts)
        {
            if (guild?.GetChannel(post.ChannelId) is not IMessageChannel channel)
            {
                error = "channel not found";
                continue;
            }

            var failure = await RunAsync(
                () => channel.DeleteMessageAsync(post.MessageId, new RequestOptions { AuditLogReason = auditReason }),
                "delete message", post.MessageId);
            if (failure is null)
            {
                deleted++;
            }
            else
            {
                error = failure;
            }
        }
        return (deleted, error);
    }

    /// <summary>The member, or null if they left the server.</summary>
    public async Task<IGuildUser?> GetMemberAsync(ulong userId)
    {
        var guildId = startupOptions.Value.GuildId;
        return (IGuildUser?)client.GetGuild(guildId)?.GetUser(userId) ?? await client.Rest.GetGuildUserAsync(guildId, userId);
    }

    /// <summary>The member as Discord has them now, or null if they left. The cache may not have seen a timeout another
    /// bot set a moment ago.</summary>
    private async Task<IGuildUser?> GetFreshMemberAsync(ulong userId) =>
        await client.Rest.GetGuildUserAsync(startupOptions.Value.GuildId, userId);

    /// <returns>Null on success (a 404 counts: whatever it was is already gone), otherwise why it failed.</returns>
    private async Task<string?> RunAsync(Func<Task> action, string what, ulong id)
    {
        try
        {
            await action();
            return null;
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == HttpStatusCode.Forbidden)
        {
            logger.LogWarning("Couldn't {Action} {Id}: {Error}", what, id, ex.Message);
            return "missing permission, or their role is above the bot's";
        }
        catch (Exception ex) when (ex is Discord.Net.HttpException or TimeoutException or InvalidOperationException)
        {
            logger.LogWarning("Couldn't {Action} {Id}: {Error}", what, id, ex.Message);
            return ex.Message;
        }
    }
}
