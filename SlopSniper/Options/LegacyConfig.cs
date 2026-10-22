using Microsoft.Extensions.Configuration;

namespace SlopSniper.Options;

/// <summary>Finds settings left over from the flat config.json used before modules had their own sections, and
/// sections under an old name. They would otherwise be ignored without a word.</summary>
public static class LegacyConfig
{
    private static readonly Dictionary<string, string> MovedSettings = new()
    {
        ["OpenAiApiKey"] = "Provenance",
        ["UserAppUserIds"] = "Provenance",
        ["ReportMode"] = "Provenance",
        ["MaxImageMiB"] = "Provenance",
        ["RequestIntervalSeconds"] = "Provenance",
        ["QueueCapacity"] = "Provenance",
        ["CacheHours"] = "Provenance",
    };

    private static readonly Dictionary<string, string> RenamedSections = new()
    {
        ["ScamImages"] = "Scams",
    };

    public static List<string> FindMovedSettings(IConfiguration configuration) =>
        MovedSettings
            .Where(m => configuration.GetSection(m.Key).Exists())
            .Select(m => $"{m.Key} has moved to {m.Value}.{m.Key} (see config.example.json).")
            .Concat(RenamedSections
                .Where(r => configuration.GetSection(r.Key).Exists())
                .Select(r => $"The {r.Key} section has been renamed to {r.Value} (see config.example.json)."))
            .ToList();
}
