using System.Numerics;
using System.Text.RegularExpressions;

namespace SlopSniper.Scams;

/// <summary>Groups of words that scam screenshots are made of.</summary>
[Flags]
public enum ScamWords
{
    None = 0,

    /// <summary>Someone famous giving something away, a win to claim.</summary>
    Lure = 1,

    /// <summary>Crypto, amounts, withdrawals.</summary>
    Money = 2,

    /// <summary>A promo code to enter.</summary>
    Code = 4,

    /// <summary>A site to sign up on.</summary>
    Site = 8,
}

/// <summary>Scores text read from images by how many word groups it hits. An ordinary screenshot hits one group now
/// and then; a fake giveaway or casino win hits three or four: a famous name giving away money, an amount in crypto, a
/// promo code, and a site to enter it on. The words come from Scams.Words.</summary>
public static class ScamKeywords
{
    // Keywords shown per post, to keep alerts short.
    private const int MaxKeywords = 8;

    // The patterns come from config, so a slow one can't hold up the OCR queue.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly ScamWords[] AllGroups = [ScamWords.Lure, ScamWords.Money, ScamWords.Code, ScamWords.Site];

    private static Compiled? _compiled;

    /// <returns>The groups the text hits, the words that hit them, and the groups whose patterns took too long.</returns>
    public static (ScamWords Groups, IReadOnlyList<string> Keywords, ScamWords TimedOut) Find(string text, ScamWordOptions words)
    {
        // OCR breaks lines anywhere, so "promo\ncode" has to match too.
        var normalized = string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var found = ScamWords.None;
        var timedOut = ScamWords.None;
        List<string> keywords = [];
        foreach (var (group, pattern) in GetCompiled(words))
        {
            try
            {
                var matches = pattern.Matches(normalized);
                if (matches.Count > 0)
                {
                    found |= group;
                    keywords.AddRange(matches.Select(m => m.Value.Trim()));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                timedOut |= group;
            }
        }
        return (found, keywords.Distinct().Take(MaxKeywords).ToList(), timedOut);
    }

    /// <summary>One regex for a group: any of its patterns, where the match doesn't start or end inside a word.</summary>
    private static Regex Build(IEnumerable<string> patterns) =>
        new(@"(?:(?<!\w)|(?!\w))(?:" + string.Join('|', patterns.Select(p => $"(?:{p})")) + @")(?:(?!\w)|(?<!\w))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    public static int Count(ScamWords words) => BitOperations.PopCount((uint)words);

    /// <summary>"lure, money, code".</summary>
    public static string Describe(ScamWords words) =>
        string.Join(", ", AllGroups.Where(g => words.HasFlag(g)).Select(g => g.ToString().ToLowerInvariant()));

    private static (ScamWords Group, Regex Pattern)[] GetCompiled(ScamWordOptions words)
    {
        // The options are a new object after each reload, so this compiles once per reload.
        var compiled = _compiled;
        if (compiled is null || !ReferenceEquals(compiled.Source, words))
        {
            compiled = new Compiled(words, words.GetGroups().Select(g => (g.Group, Build(g.Patterns))).ToArray());
            _compiled = compiled;
        }
        return compiled.Groups;
    }

    private sealed record Compiled(ScamWordOptions Source, (ScamWords Group, Regex Pattern)[] Groups);
}
