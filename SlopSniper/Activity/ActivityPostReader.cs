using System.Net;
using Discord;
using Discord.Net;

namespace SlopSniper.Activity;

/// <summary>A post and the message it followed or replied to. <see cref="Prior"/> is null if nothing came before it.</summary>
public sealed record PostPair(IMessage Post, IMessage? Prior, bool IsReply);

/// <summary>A member's posts, newest first. <see cref="Total"/> is how many Discord counted, which can be more than it
/// returned.</summary>
public sealed record PostsRead(List<IMessage> Posts, int Total, bool StillIndexing);

public static class ActivityPostReader
{
    private const int PageSize = DiscordConfig.MaxGuildMessageSearchLimit;

    /// <summary>Pages through a member's posts, up to <paramref name="maxPosts"/>. Null if Discord hasn't indexed the
    /// server yet.</summary>
    /// <remarks>Discord can return short or empty pages when it hasn't loaded older messages recently, so paging goes
    /// by its total and never stops on a page's length. <paramref name="newestId"/> keeps new messages from shifting
    /// the offsets.</remarks>
    public static async Task<PostsRead?> ReadAsync(Func<SearchGuildMessages, Task<GuildMessageSearchData?>> search, ulong userId, int maxPosts, ulong newestId)
    {
        List<IMessage> posts = [];
        var total = int.MaxValue;
        var stillIndexing = false;
        for (var offset = 0; offset < maxPosts && offset < total; offset += PageSize)
        {
            var result = await search(new SearchGuildMessages
            {
                AuthorIds = new Optional<IEnumerable<ulong>>([userId]),
                Limit = PageSize,
                Offset = offset,
                MaxMessageId = newestId,
                SortAlgorithm = SortAlgorithm.Timestamp,
                SortDirection = SortDirection.Descending,
                IncludeNsfw = true,
            });
            if (result is null)
            {
                return offset == 0 ? null : new PostsRead(posts, total, StillIndexing: true);
            }

            total = result.TotalResults;
            stillIndexing |= result.DoingDeepHistoricalIndex;
            posts.AddRange(result.Messages.Where(m => m.Author.Id == userId));
        }
        return new PostsRead(posts, total, stillIndexing);
    }

    /// <summary>Finds what each post followed. One fetch covers every post inside the messages it returns, so posts
    /// close together share a request.</summary>
    /// <param name="fetchBefore">The messages before a post, newest first.</param>
    /// <param name="outOfTime">Checked before each fetch. When true, the pairs found so far are returned.</param>
    public static async Task<List<PostPair>> PairWithPriorAsync(IReadOnlyList<IMessage> posts, Func<IMessage, Task<IReadOnlyList<IMessage>>> fetchBefore, Func<bool> outOfTime)
    {
        List<PostPair> pairs = [];
        foreach (var channel in posts.GroupBy(p => p.Channel.Id))
        {
            var pending = channel.OrderByDescending(p => p.Id).ToList();
            try
            {
                for (var index = 0; index < pending.Count;)
                {
                    if (TryPairReply(pending[index], pairs))
                    {
                        index++;
                        continue;
                    }
                    if (outOfTime())
                    {
                        return pairs;
                    }

                    var history = await fetchBefore(pending[index]);
                    pairs.Add(new PostPair(pending[index], history.FirstOrDefault(), IsReply: false));
                    index++;

                    // Older posts inside this page need no fetch of their own, as long as the message before them is
                    // in the page too.
                    for (; index < pending.Count; index++)
                    {
                        if (TryPairReply(pending[index], pairs))
                        {
                            continue;
                        }

                        var position = history.ToList().FindIndex(m => m.Id == pending[index].Id);
                        if (position < 0 || position == history.Count - 1)
                        {
                            break;
                        }
                        pairs.Add(new PostPair(pending[index], history[position + 1], IsReply: false));
                    }
                }
            }
            catch (HttpException ex) when (ex.HttpCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                // A channel the bot can search but not read, or one deleted since. The others still count.
            }
        }
        return pairs;
    }

    // A reply carries the message it answers, so it needs no fetch.
    private static bool TryPairReply(IMessage post, List<PostPair> pairs)
    {
        if (post is not IUserMessage { ReferencedMessage: { } referenced })
        {
            return false;
        }
        pairs.Add(new PostPair(post, referenced, IsReply: true));
        return true;
    }
}
