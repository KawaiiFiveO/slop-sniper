using Discord;

namespace SlopSniper.Activity;

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
}
