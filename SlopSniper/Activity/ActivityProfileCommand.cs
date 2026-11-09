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
    private const int LookbackDays = 90;
    private const int PageSize = DiscordConfig.MaxGuildMessageSearchLimit;

    // Every sampled post costs a request for its context, so these set how long a profile takes.
    private const int MemberPosts = 500;
    private const int MemberSample = 60;
    private const int RegularPosts = 100;
    private const int RegularSample = 25;
    private const int MaxRegulars = 3;
    private const int MinAppearances = 3;

    // Enough to find the last real message when system messages (joins, pins) are in the way.
    private const int ContextMessages = 3;

    private const int IndexRetries = 5;
    private const int MaxIndexWaitSeconds = 15;

    // Follow-ups stop working 15 minutes after the command.
    private static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(5);

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
            await RespondAsync("Another profile is being built. Try again in a minute or two.", ephemeral: true);
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
        var since = SnowflakeUtils.ToSnowflake(time.GetUtcNow() - TimeSpan.FromDays(LookbackDays));
        if (await ReadPostsAsync(userId, MemberPosts, since, options) is not { } posts)
        {
            return "Discord is still indexing this server's messages for search. Try again in a few minutes.";
        }
        if (posts.Count == 0)
        {
            return $"Found no posts from <@{userId}> in the last {LookbackDays} days in channels the bot can read.";
        }

        Dictionary<ulong, int> partners = [];
        var profile = await ProfileAsync(userId, posts, MemberSample, partners, options);

        List<ActivityProfile> regulars = [];
        foreach (var (regularId, _) in partners.Where(p => p.Value >= MinAppearances).OrderByDescending(p => p.Value).Take(MaxRegulars))
        {
            if (await ReadPostsAsync(regularId, RegularPosts, since, options) is { Count: > 0 } regularPosts)
            {
                regulars.Add(await ProfileAsync(regularId, regularPosts, RegularSample, null, options));
            }
        }

        return ActivityFormatter.Format(userId, LookbackDays, profile, regulars);
    }

    /// <summary>Newest first, or null while Discord hasn't indexed the server yet.</summary>
    private async Task<List<IMessage>?> ReadPostsAsync(ulong userId, int maxPosts, ulong since, RequestOptions options)
    {
        List<IMessage> posts = [];
        Optional<ulong> before = default;
        for (var page = 0; page < maxPosts / PageSize; page++)
        {
            var result = await SearchAsync(new SearchGuildMessages
            {
                AuthorIds = new Optional<IEnumerable<ulong>>([userId]),
                Limit = PageSize,
                MinMessageId = since,
                MaxMessageId = before,
                SortAlgorithm = SortAlgorithm.Timestamp,
                SortDirection = SortDirection.Descending,
                IncludeNsfw = true,
            }, options);
            if (result is null)
            {
                return page == 0 ? null : posts;
            }
            if (result.Messages.Count == 0)
            {
                break;
            }

            posts.AddRange(result.Messages.Where(m => m.Author.Id == userId));
            before = result.Messages.Min(m => m.Id);
        }
        return posts;
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

    /// <summary>Checks an even spread of the posts, so one busy afternoon doesn't dominate. Adds the people the posts
    /// answered to <paramref name="partners"/>.</summary>
    private async Task<ActivityProfile> ProfileAsync(ulong userId, List<IMessage> posts, int sampleSize, Dictionary<ulong, int>? partners, RequestOptions options)
    {
        var withText = posts.Where(p => p.Source != MessageSource.System && !string.IsNullOrWhiteSpace(p.Content)).ToList();
        var step = Math.Max(1.0, (double)withText.Count / sampleSize);

        List<SampledPost> sample = [];
        for (var position = 0.0; position < withText.Count && sample.Count < sampleSize; position += step)
        {
            var post = withText[(int)position];
            if (post.Channel is null)
            {
                continue;
            }

            try
            {
                var (prior, priorAuthor, isReply) = await FindPriorAsync(post, userId, options);
                sample.Add(new SampledPost(post.Content, post.Timestamp, isReply, prior));
                if (partners is not null && priorAuthor is { IsBot: false, IsWebhook: false } && priorAuthor.Id != userId)
                {
                    partners[priorAuthor.Id] = partners.GetValueOrDefault(priorAuthor.Id) + 1;
                }
            }
            catch (HttpException ex) when (ex.HttpCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            {
                // A channel the bot can search but not read, or one deleted since. The others still count.
            }
        }

        return ActivityAnalyzer.Analyze(posts.Count, posts.Count(p => p.EditedTimestamp is not null), sample);
    }

    private async Task<(PriorPost? Prior, IUser? Author, bool IsReply)> FindPriorAsync(IMessage post, ulong userId, RequestOptions options)
    {
        if (post is IUserMessage { ReferencedMessage: { } referenced })
        {
            return (ToPrior(referenced, userId), referenced.Author, true);
        }

        var before = await post.Channel.GetMessagesAsync(post.Id, Direction.Before, ContextMessages, CacheMode.AllowDownload, options).FlattenAsync();
        var previous = before.Where(m => m.Source != MessageSource.System).MaxBy(m => m.Id);
        return previous is null ? (null, null, false) : (ToPrior(previous, userId), previous.Author, false);
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
