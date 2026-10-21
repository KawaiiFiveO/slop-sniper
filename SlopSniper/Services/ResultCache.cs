using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Provenance;

namespace SlopSniper.Services;

/// <summary>Recent results keyed by the file's SHA-256, so a reposted image doesn't cost another API call.</summary>
public sealed class ResultCache(IOptionsMonitor<BotOptions> options) : IDisposable
{
    private const int MaxEntries = 10_000;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });

    public bool TryGet(string hash, out ProvenanceCheck? result)
    {
        result = null;
        return options.CurrentValue.CacheHours > 0 && _cache.TryGetValue(hash, out result) && result is not null;
    }

    public void Set(string hash, ProvenanceCheck result)
    {
        var hours = options.CurrentValue.CacheHours;
        if (hours > 0)
        {
            _cache.Set(hash, result, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(hours),
            });
        }
    }

    public void Dispose() => _cache.Dispose();
}
