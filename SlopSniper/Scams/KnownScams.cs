using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.State;

namespace SlopSniper.Scams;

/// <summary>How a known scam image or domain was learned.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScamOrigin>))]
public enum ScamOrigin
{
    /// <summary>From a post the bot acted on by itself.</summary>
    Detected,

    /// <summary>A moderator used "Report as scam".</summary>
    Reported,

    /// <summary>A moderator confirmed a "possible scam" alert.</summary>
    Confirmed,
}

/// <param name="EventId">The detection it was learned from, so Undo can forget it again.</param>
public sealed record KnownScamImage(ulong Hash, ulong EventId, ScamOrigin Origin, DateTimeOffset AddedAt);

/// <param name="Domain">Normalized (see <see cref="ScamLinks.Normalize"/>). Its subdomains match too.</param>
public sealed record KnownScamDomain(string Domain, ulong EventId, ScamOrigin Origin, DateTimeOffset AddedAt);

/// <summary>How many known scam images and domains were learned or forgotten.</summary>
public readonly record struct KnownCounts(int Images, int Domains)
{
    public int Total => Images + Domains;
}

/// <summary>Perceptual hashes of confirmed scam images and the domains their posts linked to, kept in state.json
/// (hashes and domains only, never images or text). Matching runs against an in-memory copy.</summary>
public sealed class KnownScams(StateStore state, TimeProvider time, IOptionsMonitor<BotOptions> options)
{
    // Domains are a few bytes each, so this only stops the list growing forever.
    private const int MaxKnownDomains = 1000;

    // Replaced as a whole on every change, so readers never see a list being modified.
    private volatile Snapshot _known = new([], new Dictionary<string, KnownScamDomain>());

    public int ImageCount => _known.Images.Count;

    public int DomainCount => _known.Domains.Count;

    public async Task LoadAsync()
    {
        _known = await state.ReadAsync(Snapshot.From);
    }

    /// <summary>How many of <paramref name="hashes"/> are within HashDistance of a known scam image.</summary>
    public int CountImageMatches(IEnumerable<ulong> hashes)
    {
        var known = _known.Images;
        var maxDistance = options.CurrentValue.Scams.HashDistance;
        return hashes.Count(h => known.Any(k => PerceptualHash.Distance(h, k.Hash) <= maxDistance));
    }

    /// <summary>The closest known scam image, for debugging. Null if none are known.</summary>
    public (KnownScamImage Known, int Distance)? FindNearestImage(ulong hash) =>
        _known.Images.Count == 0
            ? null
            : _known.Images.Select(k => (k, PerceptualHash.Distance(hash, k.Hash))).MinBy(m => m.Item2);

    /// <summary>How many of <paramref name="domains"/> are, or belong to, a known scam domain. Cheap enough for the
    /// gateway thread.</summary>
    public int CountDomainMatches(IEnumerable<string> domains) => domains.Count(d => FindDomain(d) is not null);

    /// <summary>The known scam domain <paramref name="domain"/> is or belongs to. Trusted domains never match, even if
    /// they were learned before being trusted.</summary>
    public KnownScamDomain? FindDomain(string domain)
    {
        var known = _known.Domains;
        if (known.Count == 0 || ScamLinks.IsTrusted(domain, options.CurrentValue.Scams.TrustedDomains))
        {
            return null;
        }
        return ScamLinks.WithParents(domain).Select(d => known.GetValueOrDefault(d)).FirstOrDefault(k => k is not null);
    }

    /// <summary>Remembers a scam post's images and untrusted domains. Exact duplicates are skipped; past the limits the
    /// oldest are dropped.</summary>
    /// <returns>How many were added.</returns>
    public Task<KnownCounts> AddAsync(IEnumerable<ulong> hashes, IEnumerable<string> domains, ulong eventId, ScamOrigin origin)
    {
        var now = time.GetUtcNow();
        var settings = options.CurrentValue.Scams;
        return UpdateAsync(s =>
        {
            var images = hashes
                .Distinct()
                .Where(h => s.KnownScamImages.All(k => k.Hash != h))
                .Select(h => new KnownScamImage(h, eventId, origin, now))
                .ToList();
            s.KnownScamImages.AddRange(images);
            Trim(s.KnownScamImages, settings.MaxKnownImages);

            var newDomains = domains
                .Where(d => !ScamLinks.IsTrusted(d, settings.TrustedDomains))
                .Distinct()
                .Where(d => s.KnownScamDomains.All(k => k.Domain != d))
                .Select(d => new KnownScamDomain(d, eventId, origin, now))
                .ToList();
            s.KnownScamDomains.AddRange(newDomains);
            Trim(s.KnownScamDomains, MaxKnownDomains);

            return new KnownCounts(images.Count, newDomains.Count);
        });
    }

    /// <summary>Forgets what was learned from a detection that a moderator undid.</summary>
    /// <returns>How many were removed.</returns>
    public Task<KnownCounts> RemoveEventAsync(ulong eventId) =>
        UpdateAsync(s => new KnownCounts(
            s.KnownScamImages.RemoveAll(k => k.EventId == eventId),
            s.KnownScamDomains.RemoveAll(k => k.EventId == eventId)));

    /// <summary>Forgets every known image that matches one of <paramref name="hashes"/>, and every known domain one of
    /// <paramref name="domains"/> is or belongs to, after a false positive.</summary>
    /// <returns>How many were removed.</returns>
    public Task<KnownCounts> RemoveMatchingAsync(IReadOnlyList<ulong> hashes, IReadOnlyList<string> domains)
    {
        var maxDistance = options.CurrentValue.Scams.HashDistance;
        var matching = domains.SelectMany(ScamLinks.WithParents).ToHashSet();
        return UpdateAsync(s => new KnownCounts(
            s.KnownScamImages.RemoveAll(k => hashes.Any(h => PerceptualHash.Distance(h, k.Hash) <= maxDistance)),
            s.KnownScamDomains.RemoveAll(k => matching.Contains(k.Domain))));
    }

    private static void Trim<T>(List<T> list, int max)
    {
        if (list.Count > max)
        {
            list.RemoveRange(0, list.Count - max);
        }
    }

    private async Task<KnownCounts> UpdateAsync(Func<BotState, KnownCounts> change)
    {
        var result = default(KnownCounts);
        await state.UpdateAsync(s =>
        {
            // Inside the state lock, so concurrent changes are applied in order.
            result = change(s);
            _known = Snapshot.From(s);
        });
        return result;
    }

    private sealed record Snapshot(IReadOnlyList<KnownScamImage> Images, IReadOnlyDictionary<string, KnownScamDomain> Domains)
    {
        public static Snapshot From(BotState state) =>
            new(state.KnownScamImages.ToList(), state.KnownScamDomains.ToDictionary(d => d.Domain));
    }
}
