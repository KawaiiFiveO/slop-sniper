using System.Text.RegularExpressions;

namespace SlopSniper.Scams;

/// <summary>The "Scams.Words" section: the word groups scam screenshots are made of (see <see cref="ScamKeywords"/>).
/// Each entry is a regex matched against the text read from an image, ignoring case, and never matches part of a word.
/// A missing or empty list uses the built-in one.</summary>
public sealed class ScamWordOptions
{
    // Null rather than the defaults: the binder would add the configured entries to a list that's already filled.
    public List<string>? Lure { get; set; }

    public List<string>? Money { get; set; }

    public List<string>? Code { get; set; }

    public List<string>? Site { get; set; }

    // The built-in lists, also written out in config.example.json.
    public static readonly IReadOnlyList<string> DefaultLure =
    [
        "giveaway", "give ?away", "giving away", @"mr\.? ?beast", "elon musk", "airdrop", "jackpot", "congratulations",
        "you(?:'ve| have)? won", "winner", "claim(?:ed)?", "reward", "free (?:crypto|bitcoin|btc|usdt|money|spins?)",
    ];

    public static readonly IReadOnlyList<string> DefaultMoney =
    [
        "usdt", "btc", "bitcoin", "eth", "ethereum", "crypto", "tether", "withdraw(?:al|n)?", "balance", "deposit",
        "casino", "payout", "usd", @"[$€£] ?\d[\d,.]*", @"\d[\d,.]* ?(?:usdt|btc|eth)",
    ];

    public static readonly IReadOnlyList<string> DefaultCode =
    [
        "promo ?codes?", "bonus ?codes?", "use (?:the |my )?code", "enter (?:the )?code", "activation code",
        "referral code", "redeem(?:ed)?", "code ?:",
    ];

    public static readonly IReadOnlyList<string> DefaultSite =
    [
        "sign ?up", "registers?", "registered", "registration", "website", "go to", "visit",
        @"[a-z0-9-]{3,}\.(?:com|io|net|org|xyz|gg|bet|casino|app|site|online|top|vip|pro|cc|win|fun|club|live|space|store|me|co|us)",
    ];

    /// <returns>Each group's patterns, the built-in ones where none are configured.</returns>
    public IReadOnlyList<(ScamWords Group, IReadOnlyList<string> Patterns)> GetGroups() =>
    [
        (ScamWords.Lure, Lure is { Count: > 0 } ? Lure : DefaultLure),
        (ScamWords.Money, Money is { Count: > 0 } ? Money : DefaultMoney),
        (ScamWords.Code, Code is { Count: > 0 } ? Code : DefaultCode),
        (ScamWords.Site, Site is { Count: > 0 } ? Site : DefaultSite),
    ];

    public void Validate(List<string> errors)
    {
        foreach (var (group, patterns) in GetGroups())
        {
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    errors.Add($"Scams.Words.{group} has an empty entry, which would match everything.");
                    continue;
                }

                try
                {
                    _ = new Regex(pattern);
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"Scams.Words.{group}: \"{pattern}\" isn't a valid regex: {ex.Message}");
                }
            }
        }
    }
}
