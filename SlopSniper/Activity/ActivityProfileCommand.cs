using Discord;
using Discord.Interactions;
using Discord.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlopSniper.Modules;

namespace SlopSniper.Activity;

/// <summary>Right-click a member > Apps > Activity profile. Compares how they post with the people they talk to, for
/// spotting accounts run by a chatbot. Only reports to the moderator who asked: it never acts, stores or logs message
/// text, and doesn't feed into scam decisions. GuildId only.</summary>
public sealed class ActivityProfileCommand(
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<ActivityProfileCommand> logger) : InteractionModuleBase<SocketInteractionContext>
{
    private const int MemberPosts = 500;
    private const int RegularPosts = 100;
    private const int MaxRegulars = 3;
    private const int MinAppearances = 3;

    private const int IndexRetries = 5;
    private const int MaxIndexWaitSeconds = 15;

    // Past these, a profile reports what it has so far instead of failing.
    private static readonly TimeSpan MemberBudget = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromMinutes(12);

    // Follow-ups stop working 15 minutes after the command.
    private static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(14);

    // Profiles make a lot of requests on a small server, so one at a time.
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    [DefaultMemberPermissions(GuildPermission.ModerateMembers)]
    [CommandContextType(InteractionContextType.Guild)]
    [RequirePermission(GuildPermission.ModerateMembers)]
    [UserCommand("Activity profile")]
    public async Task Profile(IUser user)
    {
        if (user.IsBot || user.IsWebhook)
        {
            await RespondAsync("That's already marked as a bot account.", ephemeral: true);
            return;
        }
        if (!OneAtATime.Wait(0))
        {
            await RespondAsync("Another profile is being built. Try again in a few minutes.", ephemeral: true);
            return;
        }

        try
        {
            await DeferAsync(ephemeral: true);
            logger.LogInformation("Activity profile of {UserId} requested by {Moderator} ({ModeratorId}).",
                user.Id, Context.User.Username, Context.User.Id);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            timeout.CancelAfter(TimeLimit);
            var options = new RequestOptions { CancelToken = timeout.Token };

            try
            {
                await FollowupAsync(await BuildReplyAsync(user.Id, options), ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            catch (OperationCanceledException) when (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                await FollowupAsync("That took too long, probably because of Discord's rate limits. Try again later.", ephemeral: true);
            }
            catch (HttpException ex)
            {
                logger.LogWarning("Activity profile of {UserId} failed: HTTP {Status}, Discord code {Code}.", user.Id, (int)ex.HttpCode, ex.DiscordCode);
                await FollowupAsync($"Discord refused the message search (HTTP {(int)ex.HttpCode}). The bot needs Read Message History in the channels it reads.", ephemeral: true);
            }
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private async Task<string> BuildReplyAsync(ulong userId, RequestOptions options)
    {
        var started = time.GetUtcNow();
        var newest = SnowflakeUtils.ToSnowflake(started);
        var read = await ActivityPostReader.ReadAsync(search => SearchAsync(search, options), userId, MemberPosts, newest);
        if (read is null)
        {
            return "Discord is still indexing this server's messages for search. Try again in a few minutes.";
        }

        logger.LogInformation("Activity profile of {UserId}: read {Read} of {Total} posts, still indexing: {Indexing}.",
            userId, read.Posts.Count, read.Total, read.StillIndexing);
        if (read.Posts.Count == 0)
        {
            return $"Found no posts from <@{userId}> in channels the bot can read.";
        }

        Dictionary<ulong, int> partners = [];
        var profile = await ProfileAsync(userId, read.Posts, started + MemberBudget, partners, options);

        List<ActivityProfile> regulars = [];
        foreach (var (regularId, _) in partners.Where(p => p.Value >= MinAppearances).OrderByDescending(p => p.Value).Take(MaxRegulars))
        {
            if (time.GetUtcNow() >= started + TotalBudget)
            {
                break;
            }
            if (await ActivityPostReader.ReadAsync(search => SearchAsync(search, options), regularId, RegularPosts, newest) is { Posts.Count: > 0 } regular)
            {
                regulars.Add(await ProfileAsync(regularId, regular.Posts, started + TotalBudget, null, options));
            }
        }

        var incomplete = read.StillIndexing || read.Posts.Count < Math.Min(read.Total, MemberPosts);
        return ActivityFormatter.Format(userId, read.Total, incomplete, profile, regulars);
    }

    private async Task<GuildMessageSearchData?> SearchAsync(SearchGuildMessages search, RequestOptions options)
    {
        for (var attempt = 0; attempt < IndexRetries; attempt++)
        {
            var result = await Context.Guild.SearchMessagesAsync(search, CacheMode.CacheOnly, options);
            if (!result.IndexNotYetAvailable)
            {
                return result;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(result.RetryAfter ?? 2, 1, MaxIndexWaitSeconds)), options.CancelToken);
        }
        return null;
    }

    /// <summary>Checks every post with text against what came before it, as far as time allows. Adds the people the
    /// posts answered to <paramref name="partners"/>.</summary>
    private async Task<ActivityProfile> ProfileAsync(ulong userId, List<IMessage> posts, DateTimeOffset deadline, Dictionary<ulong, int>? partners, RequestOptions options)
    {
        var withText = posts.Where(p => p.Source != MessageSource.System && !string.IsNullOrWhiteSpace(p.Content) && p.Channel is not null).ToList();
        var pairs = await ActivityPostReader.PairWithPriorAsync(withText, post => FetchBeforeAsync(post, options), () => time.GetUtcNow() >= deadline);

        List<SampledPost> sample = [];
        foreach (var (post, prior, isReply) in pairs)
        {
            sample.Add(new SampledPost(post.Content, post.Timestamp, isReply, prior is null ? null : ToPrior(prior, userId)));
            if (partners is not null && prior?.Author is { IsBot: false, IsWebhook: false } author && author.Id != userId)
            {
                partners[author.Id] = partners.GetValueOrDefault(author.Id) + 1;
            }
        }

        return ActivityAnalyzer.Analyze(posts.Count, posts.Count(p => p.EditedTimestamp is not null), sample);
    }

    /// <summary>The messages before a post, newest first, leaving out system messages (joins, pins).</summary>
    private static async Task<IReadOnlyList<IMessage>> FetchBeforeAsync(IMessage post, RequestOptions options)
    {
        var before = await post.Channel.GetMessagesAsync(post.Id, Direction.Before, DiscordConfig.MaxMessagesPerBatch, CacheMode.AllowDownload, options).FlattenAsync();
        return before.Where(m => m.Source != MessageSource.System).OrderByDescending(m => m.Id).ToList();
    }

    private PriorPost ToPrior(IMessage message, ulong userId) =>
        new(message.Author.Id == userId, message.Content ?? string.Empty, message.Timestamp, Names(message.Author));

    private List<string> Names(IUser author)
    {
        // History doesn't include nicknames, so this only finds them for members the bot has cached.
        var nickname = (author as IGuildUser)?.Nickname ?? Context.Guild.GetUser(author.Id)?.Nickname;
        return new[] { author.Username, author.GlobalName, nickname, author.Id.ToString() }.OfType<string>().ToList();
    }
}
