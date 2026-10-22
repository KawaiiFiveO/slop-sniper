using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.State;

namespace SlopSniper.ScamImages;

/// <summary>How a known scam image was learned.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScamImageOrigin>))]
public enum ScamImageOrigin
{
    /// <summary>From a post the bot acted on by itself.</summary>
    Detected,

    /// <summary>A moderator used "Report as scam".</summary>
    Reported,

    /// <summary>A moderator confirmed a "possible scam" alert.</summary>
    Confirmed,
}

/// <param name="EventId">The detection it was learned from, so Undo can forget it again.</param>
public sealed record KnownScamImage(ulong Hash, ulong EventId, ScamImageOrigin Origin, DateTimeOffset AddedAt);

/// <summary>Perceptual hashes of confirmed scam images, kept in state.json (hashes only, never images). Matching
/// runs against an in-memory copy.</summary>
public sealed class ScamHashStore(StateStore state, TimeProvider time, IOptionsMonitor<BotOptions> options)
{
    // Replaced as a whole on every change, so readers never see a list being modified.
    private volatile IReadOnlyList<KnownScamImage> _known = [];

    public int Count => _known.Count;

    public async Task LoadAsync()
    {
        _known = await state.ReadAsync(s => s.KnownScamImages.ToList());
    }

    /// <summary>How many of <paramref name="hashes"/> are within HashDistance of a known scam image.</summary>
    public int CountMatches(IEnumerable<ulong> hashes)
    {
        var known = _known;
        var maxDistance = options.CurrentValue.ScamImages.HashDistance;
        return hashes.Count(h => known.Any(k => PerceptualHash.Distance(h, k.Hash) <= maxDistance));
    }

    /// <summary>The closest known scam image, for debugging. Null if none are known.</summary>
    public (KnownScamImage Known, int Distance)? FindNearest(ulong hash) =>
        _known.Count == 0
            ? null
            : _known.Select(k => (k, PerceptualHash.Distance(hash, k.Hash))).MinBy(m => m.Item2);

    /// <summary>Remembers new hashes. Exact duplicates are skipped; past MaxKnownImages the oldest are dropped.</summary>
    /// <returns>How many were added.</returns>
    public Task<int> AddAsync(IEnumerable<ulong> hashes, ulong eventId, ScamImageOrigin origin)
    {
        var now = time.GetUtcNow();
        var max = options.CurrentValue.ScamImages.MaxKnownImages;
        return UpdateAsync(known =>
        {
            var added = hashes
                .Distinct()
                .Where(h => known.All(k => k.Hash != h))
                .Select(h => new KnownScamImage(h, eventId, origin, now))
                .ToList();
            known.AddRange(added);
            if (known.Count > max)
            {
                known.RemoveRange(0, known.Count - max);
            }
            return added.Count;
        });
    }

    /// <summary>Forgets what was learned from a detection that a moderator undid.</summary>
    /// <returns>How many were removed.</returns>
    public Task<int> RemoveEventAsync(ulong eventId) =>
        UpdateAsync(known => known.RemoveAll(k => k.EventId == eventId));

    /// <summary>Forgets every known image that matches one of <paramref name="hashes"/>, after a false positive.</summary>
    /// <returns>How many were removed.</returns>
    public Task<int> RemoveMatchingAsync(IReadOnlyList<ulong> hashes)
    {
        var maxDistance = options.CurrentValue.ScamImages.HashDistance;
        return UpdateAsync(known => known.RemoveAll(k => hashes.Any(h => PerceptualHash.Distance(h, k.Hash) <= maxDistance)));
    }

    private async Task<int> UpdateAsync(Func<List<KnownScamImage>, int> change)
    {
        var result = 0;
        await state.UpdateAsync(s =>
        {
            // Inside the state lock, so concurrent changes are applied in order.
            result = change(s.KnownScamImages);
            _known = s.KnownScamImages.ToList();
        });
        return result;
    }
}
