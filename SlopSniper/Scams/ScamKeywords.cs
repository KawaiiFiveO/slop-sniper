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
/// promo code, and a site to enter it on.</summary>
public static partial class ScamKeywords
{
    // Keywords shown per post, to keep alerts short.
    private const int MaxKeywords = 8;

    [GeneratedRegex(@"\b(?:giveaway|give ?away|giving away|mr\.? ?beast|elon musk|airdrop|jackpot|congratulations|you(?:'ve| have)? won|winner|claim(?:ed)?|reward|free (?:crypto|bitcoin|btc|usdt|money|spins?))\b")]
    private static partial Regex Lure();

    [GeneratedRegex(@"\b(?:usdt|btc|bitcoin|eth|ethereum|crypto|tether|withdraw(?:al|n)?|balance|deposit|casino|payout|usd)\b|[$€£] ?\d[\d,.]*|\d[\d,.]* ?(?:usdt|btc|eth)\b")]
    private static partial Regex Money();

    [GeneratedRegex(@"\b(?:promo ?codes?|bonus ?codes?|use (?:the |my )?code|enter (?:the )?code|activation code|referral code|redeem(?:ed)?)\b|\bcode ?:")]
    private static partial Regex Code();

    [GeneratedRegex(@"\b(?:sign ?up|registers?|registered|registration|website|go to|visit)\b|\b[a-z0-9-]{3,}\.(?:com|io|net|org|xyz|gg|bet|casino|app|site|online|top|vip|pro|cc|win|fun|club|live|space|store|me|co|us)\b")]
    private static partial Regex Site();

    private static readonly (ScamWords Group, Func<Regex> Pattern)[] Groups =
        [(ScamWords.Lure, Lure), (ScamWords.Money, Money), (ScamWords.Code, Code), (ScamWords.Site, Site)];

    /// <returns>The groups the text hits, and the words that hit them.</returns>
    public static (ScamWords Groups, IReadOnlyList<string> Keywords) Find(string text)
    {
        // OCR breaks lines anywhere, so "promo\ncode" has to match too.
        var normalized = string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var found = ScamWords.None;
        List<string> keywords = [];
        foreach (var (group, pattern) in Groups)
        {
            var matches = pattern().Matches(normalized);
            if (matches.Count > 0)
            {
                found |= group;
                keywords.AddRange(matches.Select(m => m.Value.Trim()));
            }
        }
        return (found, keywords.Distinct().Take(MaxKeywords).ToList());
    }

    public static int Count(ScamWords words) => BitOperations.PopCount((uint)words);

    /// <summary>"lure, money, code".</summary>
    public static string Describe(ScamWords words) =>
        string.Join(", ", Groups.Where(g => words.HasFlag(g.Group)).Select(g => g.Group.ToString().ToLowerInvariant()));
}
