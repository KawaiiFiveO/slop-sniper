using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>A message the bot may delete.</summary>
public readonly record struct PostRef(ulong ChannelId, ulong MessageId);

/// <param name="Channels">Distinct channels the images or link text were posted to within the window, including this
/// one.</param>
/// <param name="Repeated">What earlier posts had in common with this one.</param>
/// <param name="Posts">This post and the author's earlier ones that share an image or link text with it.</param>
/// <param name="FlaggedEventId">Set if the author was already acted on and is still flagged.</param>
public sealed record BurstResult(int Channels, Repeated Repeated, IReadOnlyList<PostRef> Posts, ulong? FlaggedEventId);

/// <summary>Remembers recent posts with images or links per author, to spot the same post going to several channels.
/// Images are matched by their attachment metadata (byte size and dimensions), so nothing is downloaded: the same
/// upload repeated by the same author has identical metadata. Text is matched by a hash, ignoring case and spacing,
/// and only for messages with a link, so repeating a plain message never counts. In memory only.</summary>
public sealed class BurstTracker(TimeProvider time, IOptionsMonitor<BotOptions> options)
{
    // After acting, more posts with images or links from the author are deleted without a new alert. Each one extends
    // this, so a hijacked account that couldn't be timed out keeps getting cleaned up.
    private static readonly TimeSpan FlagDuration = TimeSpan.FromHours(1);

    // Bounds memory during a flood. Past this, posts by new authors aren't tracked until older ones expire.
    private const int MaxAuthors = 2000;

    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, List<Post>> _posts = [];
    private readonly Dictionary<ulong, FlagEntry> _flags = [];

    /// <param name="linkText">The message's text if it has a link, otherwise null.</param>
    public BurstResult Record(ulong authorId, PostRef post, IReadOnlyList<ImageSource> images, string? linkText)
    {
        var now = time.GetUtcNow();
        var window = TimeSpan.FromSeconds(options.CurrentValue.Scams.BurstWindowSeconds);
        var fingerprints = GetFingerprints(images, linkText);

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
                    return new BurstResult(1, Repeated.None, [post], flaggedEventId);
                }
                _posts[authorId] = recent = [];
            }

            recent.RemoveAll(p => p.At < now - window);
            var (matching, repeated) = FindMatching(recent, fingerprints);
            recent.Add(new Post(post, fingerprints, now));

            var channels = matching.Select(p => p.Ref.ChannelId).Append(post.ChannelId).Distinct().Count();
            return new BurstResult(channels, repeated, [.. matching.Select(p => p.Ref), post], flaggedEventId);
        }
    }

    /// <summary>What <see cref="Record"/> would say about this content right now, without recording anything. For the
    /// debug command.</summary>
    /// <returns>Distinct channels the author posted any of it to within the window, and whether they're flagged.</returns>
    public (int Channels, Repeated Repeated, bool Flagged) Inspect(ulong authorId, IReadOnlyList<ImageSource> images, string? linkText)
    {
        var now = time.GetUtcNow();
        var cutoff = now - TimeSpan.FromSeconds(options.CurrentValue.Scams.BurstWindowSeconds);
        var fingerprints = GetFingerprints(images, linkText);
        lock (_lock)
        {
            var flagged = _flags.TryGetValue(authorId, out var flag) && flag.Until > now;
            if (!_posts.TryGetValue(authorId, out var recent))
            {
                return (0, Repeated.None, flagged);
            }
            var (matching, repeated) = FindMatching(recent.Where(p => p.At >= cutoff), fingerprints);
            return (matching.Select(p => p.Ref.ChannelId).Distinct().Count(), repeated, flagged);
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

    private static HashSet<Fingerprint> GetFingerprints(IReadOnlyList<ImageSource> images, string? linkText)
    {
        var fingerprints = images
            .Where(i => i.Size is not null)
            .Select(i => new Fingerprint(Repeated.Images, i.Size!.Value, i.Width, i.Height))
            .ToHashSet();
        if (linkText is not null)
        {
            // Randomized per process, which is fine for something kept in memory for a minute.
            var normalized = string.Join(' ', linkText.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            fingerprints.Add(new Fingerprint(Repeated.LinkText, normalized.GetHashCode(StringComparison.Ordinal), null, null));
        }
        return fingerprints;
    }

    private static (List<Post> Matching, Repeated Repeated) FindMatching(IEnumerable<Post> recent, HashSet<Fingerprint> fingerprints)
    {
        List<Post> matching = [];
        var repeated = Repeated.None;
        foreach (var post in recent)
        {
            var shared = post.Fingerprints.Where(fingerprints.Contains).Aggregate(Repeated.None, (all, f) => all | f.Kind);
            if (shared != Repeated.None)
            {
                matching.Add(post);
                repeated |= shared;
            }
        }
        return (matching, repeated);
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

    private readonly record struct Fingerprint(Repeated Kind, long Value, int? Width, int? Height);

    private sealed record Post(PostRef Ref, HashSet<Fingerprint> Fingerprints, DateTimeOffset At);

    private readonly record struct FlagEntry(ulong EventId, DateTimeOffset Until);
}
