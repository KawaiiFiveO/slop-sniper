using System.Net;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;

namespace SlopSniper.ScamImages;

/// <param name="Deleted">Copies deleted, including ones that were already gone.</param>
/// <param name="DeleteError">Why some copies couldn't be deleted, if any couldn't.</param>
/// <param name="TimedOutFor">Null if the author wasn't timed out.</param>
/// <param name="TimeoutError">Why the timeout failed, if it was attempted.</param>
public sealed record ScamActions(int Deleted, int ToDelete, string? DeleteError, TimeSpan? TimedOutFor, string? TimeoutError);

/// <summary>Deletes scam posts and times out their author. Failures are collected for the alert, never thrown.</summary>
public sealed class ScamModerator(
    DiscordSocketClient client,
    IOptions<BotOptions> startupOptions,
    IOptionsMonitor<BotOptions> options,
    ILogger<ScamModerator> logger)
{
    private const string FollowUpReason = "slop-sniper: more posts from an account already acted on for scam images";

    /// <param name="auditReason">Shown in Discord's audit log for the deletes and the timeout.</param>
    public async Task<ScamActions> ActAsync(ulong authorId, IReadOnlyList<PostRef> posts, string auditReason)
    {
        var settings = options.CurrentValue.ScamImages;

        var (deleted, deleteError) = settings.DeleteMessages ? await DeleteAsync(posts, auditReason) : (0, null);

        TimeSpan? timedOutFor = null;
        string? timeoutError = null;
        if (settings.TimeoutMinutes > 0)
        {
            var duration = TimeSpan.FromMinutes(settings.TimeoutMinutes);
            timeoutError = await RunAsync(async () =>
            {
                var user = await GetMemberAsync(authorId) ?? throw new InvalidOperationException("they already left the server");
                await user.SetTimeOutAsync(duration, new RequestOptions { AuditLogReason = auditReason });
            }, "time out user", authorId);
            timedOutFor = timeoutError is null ? duration : null;
        }

        return new ScamActions(deleted, settings.DeleteMessages ? posts.Count : 0, deleteError, timedOutFor, timeoutError);
    }

    /// <summary>Deletes one more copy from an author who was already acted on.</summary>
    public async Task<bool> DeleteFollowUpAsync(PostRef post)
    {
        if (!options.CurrentValue.ScamImages.DeleteMessages)
        {
            return false;
        }
        var (deleted, _) = await DeleteAsync([post], FollowUpReason);
        return deleted == 1;
    }

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

        var settings = options.CurrentValue.ScamImages;
        if (settings.DeleteMessages && !permissions.ManageMessages)
        {
            logger.LogWarning("ScamImages: the bot lacks Manage Messages, so it can't delete scam posts.");
        }
        if (settings.TimeoutMinutes > 0 && !permissions.ModerateMembers)
        {
            logger.LogWarning("ScamImages: the bot lacks Moderate Members, so it can't time out scam posters.");
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
