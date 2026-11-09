using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SlopSniper.Activity;

/// <summary>The message a post followed or replied to.</summary>
public sealed record PriorPost(bool Own, string Text, DateTimeOffset At, IReadOnlyList<string> AuthorNames);

/// <summary>A post checked against its context. <see cref="Prior"/> is null when nothing came before it.</summary>
/// <remarks><see cref="Recent"/> is what other people said shortly before, newest first.</remarks>
public sealed record SampledPost(string Text, DateTimeOffset At, bool IsReply, PriorPost? Prior, IReadOnlyList<PriorPost> Recent, bool UsedReply);

/// <summary>When a post was made and whether it was edited later.</summary>
public sealed record PostStamp(DateTimeOffset At, bool Edited);

/// <summary>Style counts for one stretch of a member's posts, starting at <see cref="From"/>.</summary>
public sealed record ActivityPeriod(DateTimeOffset From, ActivityProfile Profile);

/// <summary>A member's whole history, split so a recent change in style isn't averaged away.</summary>
public sealed record MemberProfile(ActivityProfile All, ActivityProfile Recent, ActivityProfile Older, DateTimeOffset RecentFrom, IReadOnlyList<ActivityPeriod> Timeline);

/// <summary>Counts for one member. Shares are worked out when formatting, so small samples stay visible.</summary>
public sealed record ActivityProfile(
    int Posts,
    int Edited,
    int Sampled,
    int Started,
    int FollowUps,
    int Responses,
    int LongReplies,
    int FastReplies,
    double? MedianCharsPerSecond,
    int Named,
    int Echoed,
    int Emoji,
    int Formula,
    int? ActiveHours,
    int? QuietHours,
    double? BusyDayBreakHours,
    int UsedReplies,
    int Addressable,
    int NameAndEmoji,
    int BrokenMentions);

/// <summary>Measures habits typical of a chatbot account: only ever answering, answering long messages faster than
/// anyone types, and replying to a template. None of it proves anything alone, so it's only shown to moderators and
/// never feeds into scam decisions.</summary>
public static partial class ActivityAnalyzer
{
    // Longer than this since the last message means the post opened a conversation rather than answering one.
    private static readonly TimeSpan QuietGap = TimeSpan.FromMinutes(30);

    // Short replies say nothing about typing speed.
    private const int LongReplyChars = 40;

    // About 120 words a minute, with no time to read the message first.
    private const double MaxTypingCharsPerSecond = 10;

    // Message timestamps are too coarse to tell very short gaps apart.
    private const double MinGapSeconds = 0.5;

    private const int NameSearchWords = 3;
    private const int MinWordLength = 3;
    private const int MinEchoWords = 3;
    private const double EchoShare = 0.4;

    // Too few posts leave most hours empty whoever wrote them.
    private const int MinPostsForHours = 50;

    // A quarter of an even spread across 24 hours, so a rare late night doesn't make an hour count as active.
    private const double QuietHourShare = 0.01;

    private const int HoursInDay = 24;

    // A day with fewer posts has long breaks whoever wrote them.
    private const int MinBusyDayPosts = 8;

    private const int RecentShareDivisor = 3;
    private const int MaxTimelinePeriods = 6;
    private const int MinPostsPerPeriod = 10;

    private static readonly FrozenSet<string> CommonWords = new[]
    {
        "the", "and", "but", "for", "are", "was", "were", "you", "your", "that", "this", "with", "have", "has", "had",
        "not", "they", "them", "what", "when", "how", "why", "who", "can", "just", "like", "from", "there", "then",
        "yes", "yeah", "nice", "hey", "haha", "lol", "its", "too", "out", "get", "got", "about", "really", "all",
    }.ToFrozenSet();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"<a?:\w+:\d+>$")]
    private static partial Regex CustomEmojiRegex();

    // Real mentions are stored as <@id>, so an @ in the text is one that never became a mention.
    [GeneratedRegex(@"(?<![\w<@])@(?!(?:everyone|here)\b)[\p{L}\p{N}_.]{2,}|<@(?![!&]?\d+>)[^>]+>", RegexOptions.IgnoreCase)]
    private static partial Regex BrokenMentionRegex();

    /// <summary>Profiles a member's whole history, then their newest third and the rest separately.</summary>
    public static MemberProfile AnalyzeMember(IReadOnlyList<PostStamp> posts, IReadOnlyList<SampledPost> sample)
    {
        var newestFirst = posts.OrderByDescending(p => p.At).ToList();
        var recentFrom = newestFirst[Math.Max(1, newestFirst.Count / RecentShareDivisor) - 1].At;

        return new MemberProfile(
            AnalyzeStretch(posts, sample),
            AnalyzeStretch(posts.Where(p => p.At >= recentFrom).ToList(), sample.Where(s => s.At >= recentFrom).ToList()),
            AnalyzeStretch(posts.Where(p => p.At < recentFrom).ToList(), sample.Where(s => s.At < recentFrom).ToList()),
            recentFrom,
            BuildTimeline(sample));
    }

    private static ActivityProfile AnalyzeStretch(IReadOnlyList<PostStamp> posts, IReadOnlyList<SampledPost> sample) =>
        Analyze(posts.Select(p => p.At).ToList(), posts.Count(p => p.Edited), sample);

    /// <summary>Equal-sized stretches, oldest first, showing when a change in style began.</summary>
    private static List<ActivityPeriod> BuildTimeline(IReadOnlyList<SampledPost> sample)
    {
        var periods = Math.Min(MaxTimelinePeriods, sample.Count / MinPostsPerPeriod);
        if (periods < 2)
        {
            return [];
        }

        var oldestFirst = sample.OrderBy(s => s.At).ToList();
        List<ActivityPeriod> timeline = [];
        for (var period = 0; period < periods; period++)
        {
            var start = period * oldestFirst.Count / periods;
            var end = (period + 1) * oldestFirst.Count / periods;
            timeline.Add(new ActivityPeriod(oldestFirst[start].At, Analyze([], 0, oldestFirst[start..end])));
        }
        return timeline;
    }

    public static ActivityProfile Analyze(IReadOnlyList<DateTimeOffset> postTimes, int edited, IReadOnlyList<SampledPost> sample)
    {
        int started = 0, followUps = 0, responses = 0, fast = 0, named = 0, echoed = 0, emoji = 0, formula = 0;
        int addressable = 0, nameAndEmoji = 0, broken = 0;
        List<double> speeds = [];

        foreach (var post in sample)
        {
            broken += HasBrokenMention(post.Text) ? 1 : 0;

            // Counted for any post with someone to talk to, including one after the member's own post: a run of
            // comments aimed at different people is part of what this looks for.
            if (post.Recent.Count > 0)
            {
                addressable++;
                var target = post.Recent.FirstOrDefault(r => OpensWithName(post.Text, r.AuthorNames));
                var hasName = target is not null;
                var hasEcho = EchoesPrior(post.Text, (target ?? post.Recent[0]).Text);
                var hasEmoji = EndsWithEmoji(post.Text);
                named += hasName ? 1 : 0;
                echoed += hasEcho ? 1 : 0;
                emoji += hasEmoji ? 1 : 0;
                nameAndEmoji += hasName && hasEmoji ? 1 : 0;
                formula += hasName && hasEcho && hasEmoji ? 1 : 0;
            }

            if (post.Prior is not { } prior || (!post.IsReply && post.At - prior.At > QuietGap))
            {
                started++;
                continue;
            }
            if (prior.Own)
            {
                followUps++;
                continue;
            }

            responses++;
            if (post.Text.Length >= LongReplyChars)
            {
                var seconds = Math.Max((post.At - prior.At).TotalSeconds, MinGapSeconds);
                var speed = post.Text.Length / seconds;
                speeds.Add(speed);
                fast += speed > MaxTypingCharsPerSecond ? 1 : 0;
            }
        }

        var (activeHours, quietHours, busyDayBreak) = MeasureHours(postTimes);
        return new ActivityProfile(postTimes.Count, edited, sample.Count, started, followUps, responses, speeds.Count, fast,
            Median(speeds), named, echoed, emoji, formula, activeHours, quietHours, busyDayBreak, sample.Count(p => p.UsedReply), addressable, nameAndEmoji, broken);
    }

    /// <summary>How much of the day a member posts in. The quiet stretch can wrap past midnight, so timezones don't
    /// matter. People usually have one of several hours, for sleep, and an always-on account doesn't.</summary>
    private static (int? Active, int? Quiet, double? BusyDayBreak) MeasureHours(IReadOnlyList<DateTimeOffset> times)
    {
        if (times.Count < MinPostsForHours)
        {
            return (null, null, null);
        }

        var counts = new int[HoursInDay];
        foreach (var time in times)
        {
            counts[time.UtcDateTime.Hour]++;
        }

        var quietBelow = times.Count * QuietHourShare;
        int longestQuiet = 0, run = 0;
        for (var hour = 0; hour < HoursInDay * 2; hour++)
        {
            run = counts[hour % HoursInDay] < quietBelow ? run + 1 : 0;
            longestQuiet = Math.Max(longestQuiet, run);
        }

        return (counts.Count(c => c >= quietBelow), Math.Min(longestQuiet, HoursInDay), MedianBusyDayBreak(times));
    }

    /// <summary>The typical longest break inside a 24-hour stretch with plenty of posts. Unlike the hour counts, a
    /// changing schedule over months can't hide a real sleep break here.</summary>
    private static double? MedianBusyDayBreak(IReadOnlyList<DateTimeOffset> times)
    {
        var sorted = times.Order().ToArray();
        List<double> breaks = [];
        for (var start = 0; start < sorted.Length; start++)
        {
            var end = sorted[start].AddHours(HoursInDay);
            var last = sorted[start];
            var longest = 0.0;
            var posts = 1;
            for (var next = start + 1; next < sorted.Length && sorted[next] < end; next++)
            {
                longest = Math.Max(longest, (sorted[next] - last).TotalHours);
                last = sorted[next];
                posts++;
            }

            if (posts >= MinBusyDayPosts)
            {
                breaks.Add(Math.Max(longest, (end - last).TotalHours));
            }
        }
        return Median(breaks);
    }

    public static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0
            ? null
            : sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>Whether the first few words address the other person, by name or mention. Allows for a greeting first
    /// and for shortened names.</summary>
    public static bool OpensWithName(string text, IReadOnlyList<string> names)
    {
        var known = names.SelectMany(Words).ToArray();
        return Words(text)
            .Take(NameSearchWords)
            .Where(IsMeaningful)
            .Any(word => known.Any(name => name.StartsWith(word, StringComparison.Ordinal)));
    }

    /// <summary>Whether the post repeats a large part of what the other person just said.</summary>
    public static bool EchoesPrior(string text, string prior)
    {
        var priorWords = Words(prior).Where(IsMeaningful).ToHashSet();
        if (priorWords.Count < MinEchoWords)
        {
            return false;
        }

        var words = Words(text).ToHashSet();
        return (double)priorWords.Count(words.Contains) / priorWords.Count >= EchoShare;
    }

    /// <summary>Whether the post tried to mention someone in text that never became a real mention.</summary>
    public static bool HasBrokenMention(string text) => BrokenMentionRegex().IsMatch(text);

    public static bool EndsWithEmoji(string text)
    {
        var trimmed = text.TrimEnd();
        if (CustomEmojiRegex().IsMatch(trimmed))
        {
            return true;
        }

        // An emoji can end in joiners, a variation selector or a skin tone. The base symbol comes before those.
        var rest = trimmed.AsSpan();
        while (Rune.DecodeLastFromUtf16(rest, out var rune, out var consumed) == System.Buffers.OperationStatus.Done)
        {
            if (rune.Value is 0xFE0F or 0x200D or 0x20E3 or (>= 0x1F3FB and <= 0x1F3FF))
            {
                rest = rest[..^consumed];
                continue;
            }

            // Below this are symbols like the copyright sign, which aren't emoji.
            return rune.Value >= 0x2190 && Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol;
        }
        return false;
    }

    private static bool IsMeaningful(string word) => word.Length >= MinWordLength && !CommonWords.Contains(word);

    private static IEnumerable<string> Words(string text) =>
        WordRegex().Matches(text).Select(m => m.Value.ToLowerInvariant());
}
