using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Discord;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>Finds the links in a message, and decides which of their domains may be learned as scam domains. Only
/// reads the text; links are never fetched.</summary>
public static partial class ScamLinks
{
    // Sites that turn up in scam posts as often as anywhere else, and URL shorteners everyone uses. Never learned as
    // scam domains, and neither are their subdomains. Scams.TrustedDomains adds to these.
    private static readonly FrozenSet<string> BuiltInTrusted = new[]
    {
        "discord.com", "discord.gg", "discordapp.com", "discordapp.net",
        "youtube.com", "youtu.be", "twitch.tv", "tenor.com", "giphy.com", "imgur.com",
        "twitter.com", "x.com", "fxtwitter.com", "vxtwitter.com", "fixupx.com", "bsky.app",
        "reddit.com", "redd.it", "github.com", "google.com", "wikipedia.org",
        "instagram.com", "tiktok.com", "facebook.com", "spotify.com", "soundcloud.com", "pixiv.net",
        "steamcommunity.com", "steampowered.com",
        "bit.ly", "tinyurl.com", "t.co", "goo.gl", "is.gd", "cutt.ly", "rb.gy", "t.ly", "shorturl.at",
    }.ToFrozenSet();

    // Stops at whitespace and at characters that end a link in Markdown, e.g. <https://…> or [text](https://…).
    [GeneratedRegex(@"https?://[^\s<>()\[\]""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>The message's text, including forwarded messages.</summary>
    public static string GetText(IMessage message) =>
        string.Join('\n', ImageFormats.WithForwarded(message).Select(m => m.Content).Where(c => c.Length > 0));

    /// <summary>The domains of the http(s) links in <paramref name="text"/>, normalized (see <see cref="Normalize"/>).</summary>
    public static IReadOnlyList<string> GetDomains(string text)
    {
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            return [];
        }

        return LinkPattern().Matches(text)
            .Select(m => Uri.TryCreate(m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '*', '_', '~', '|'), UriKind.Absolute, out var uri)
                ? Normalize(uri.IdnHost)
                : "")
            .Where(d => d.Contains('.'))
            .Distinct()
            .ToList();
    }

    /// <summary>Lowercase, without a trailing dot or a leading "www.". Internationalized domains stay in their
    /// "xn--" form, which shows lookalike letters for what they are.</summary>
    public static string Normalize(string domain)
    {
        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized.StartsWith("www.", StringComparison.Ordinal) ? normalized[4..] : normalized;
    }

    /// <summary>The domain and its parents, down to two labels: "a.b.example.com", "b.example.com", "example.com".</summary>
    public static IEnumerable<string> WithParents(string domain)
    {
        for (var current = domain; current.Contains('.'); current = current[(current.IndexOf('.') + 1)..])
        {
            yield return current;
        }
    }

    /// <summary>Whether the domain, or a domain it belongs to, is on the built-in list or in
    /// <paramref name="trustedDomains"/>.</summary>
    public static bool IsTrusted(string domain, IReadOnlyCollection<string> trustedDomains) =>
        WithParents(domain).Any(d => BuiltInTrusted.Contains(d) || trustedDomains.Any(t => Normalize(t) == d));
}
