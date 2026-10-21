using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.ScamImages;

/// <summary>A message the bot may delete.</summary>
public readonly record struct PostRef(ulong ChannelId, ulong MessageId);

/// <param name="Channels">Distinct channels the images were posted to within the window, including this one.</param>
/// <param name="Posts">This post and the author's earlier ones that share an image with it.</param>
/// <param name="FlaggedEventId">Set if the author was already acted on and is still flagged.</param>
public sealed record BurstResult(int Channels, IReadOnlyList<PostRef> Posts, ulong? FlaggedEventId);

/// <summary>Remembers recent image posts per author, to spot the same images being posted to several channels.
/// Images are matched by their attachment metadata (byte size and dimensions), so nothing is downloaded: the same
/// upload repeated by the same author has identical metadata. In memory only.</summary>
public sealed class BurstTracker(TimeProvider time, IOptionsMonitor<BotOptions> options)
{
    // After acting, more image posts from the author are deleted without a new alert. Each one extends this, so a
    // hijacked account that couldn't be timed out keeps getting cleaned up.
    private static readonly TimeSpan FlagDuration = TimeSpan.FromHours(1);

    // Bounds memory during a flood. Past this, posts by new authors aren't tracked until older ones expire.
    private const int MaxAuthors = 2000;

    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, List<Post>> _posts = [];
    private readonly Dictionary<ulong, FlagEntry> _flags = [];

    public BurstResult Record(ulong authorId, PostRef post, IReadOnlyList<ImageSource> images)
    {
        var now = time.GetUtcNow();
        var window = TimeSpan.FromSeconds(options.CurrentValue.ScamImages.BurstWindowSeconds);
        var fingerprints = images
            .Where(i => i.Size is not null)
            .Select(i => new Fingerprint(i.Size!.Value, i.Width, i.Height))
            .ToHashSet();

        lock (_lock)
        {
            ulong? flaggedEventId = null;
            if (_flags.TryGetValue(authorId, out var flag))
            {
                if (flag.Until > now)
                {
                    flaggedEventId = flag.EventId;
                    _flags[authorId] = flag with { Until = now + FlagDuration };
                }
                else
                {
                    _flags.Remove(authorId);
                }
            }

            if (!_posts.TryGetValue(authorId, out var recent))
            {
                if (_posts.Count >= MaxAuthors)
                {
                    RemoveExpired(now - window);
                }
                if (_posts.Count >= MaxAuthors)
                {
                    return new BurstResult(1, [post], flaggedEventId);
                }
                _posts[authorId] = recent = [];
            }

            recent.RemoveAll(p => p.At < now - window);
            var matching = recent.Where(p => p.Fingerprints.Overlaps(fingerprints)).ToList();
            recent.Add(new Post(post, fingerprints, now));

            var channels = matching.Select(p => p.Ref.ChannelId).Append(post.ChannelId).Distinct().Count();
            return new BurstResult(channels, [.. matching.Select(p => p.Ref), post], flaggedEventId);
        }
    }

    /// <summary>Marks the author as acted on, unless they already are.</summary>
    /// <returns>The event the author is flagged under: <paramref name="eventId"/>, or the existing one.</returns>
    public ulong Flag(ulong authorId, ulong eventId)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_flags.TryGetValue(authorId, out var flag) && flag.Until > now)
            {
                return flag.EventId;
            }
            _flags[authorId] = new FlagEntry(eventId, now + FlagDuration);
            return eventId;
        }
    }

    /// <summary>Forgets the author after a moderator undid the action, so their posts are treated normally again.</summary>
    public void Unflag(ulong authorId)
    {
        lock (_lock)
        {
            _flags.Remove(authorId);
            _posts.Remove(authorId);
        }
    }

    private void RemoveExpired(DateTimeOffset cutoff)
    {
        foreach (var (authorId, posts) in _posts.ToList())
        {
            posts.RemoveAll(p => p.At < cutoff);
            if (posts.Count == 0)
            {
                _posts.Remove(authorId);
            }
        }
    }

    private readonly record struct Fingerprint(long Size, int? Width, int? Height);

    private sealed record Post(PostRef Ref, HashSet<Fingerprint> Fingerprints, DateTimeOffset At);

    private readonly record struct FlagEntry(ulong EventId, DateTimeOffset Until);
}
