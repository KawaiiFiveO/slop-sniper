using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SlopSniper.Activity;

/// <summary>The message a post followed or replied to.</summary>
public sealed record PriorPost(bool Own, string Text, DateTimeOffset At, IReadOnlyList<string> AuthorNames);

/// <summary>A post checked against its context. <see cref="Prior"/> is null when nothing came before it.</summary>
public sealed record SampledPost(string Text, DateTimeOffset At, bool IsReply, PriorPost? Prior);

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
    int Formula);

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

    public static ActivityProfile Analyze(int posts, int edited, IReadOnlyList<SampledPost> sample)
    {
        int started = 0, followUps = 0, responses = 0, fast = 0, named = 0, echoed = 0, emoji = 0, formula = 0;
        List<double> speeds = [];

        foreach (var post in sample)
        {
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

            var hasName = OpensWithName(post.Text, prior.AuthorNames);
            var hasEcho = EchoesPrior(post.Text, prior.Text);
            var hasEmoji = EndsWithEmoji(post.Text);
            named += hasName ? 1 : 0;
            echoed += hasEcho ? 1 : 0;
            emoji += hasEmoji ? 1 : 0;
            formula += hasName && hasEcho && hasEmoji ? 1 : 0;
        }

        return new ActivityProfile(posts, edited, sample.Count, started, followUps, responses, speeds.Count, fast,
            Median(speeds), named, echoed, emoji, formula);
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
