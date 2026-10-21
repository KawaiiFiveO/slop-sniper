using SlopSniper.Provenance;

namespace SlopSniper.Options;

/// <summary>Settings bound from config.json (and SLOPSNIPER_* environment variables). Each module has its own
/// section.</summary>
public sealed class BotOptions
{
    public const string FileName = "config.json";

    public string Token { get; set; } = "";

    /// <summary>The server that's scanned automatically and gets /sniper. 0 runs as a user app only.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Moderator channel for reports and alerts. Required when GuildId is set.</summary>
    public ulong LogChannelId { get; set; }

    /// <summary>Channels or categories that aren't scanned automatically. Threads follow their parent channel.</summary>
    public List<ulong> ExcludedChannelIds { get; set; } = [];

    /// <summary>Also scan messages from other bots. Webhook messages (e.g. PluralKit) are always scanned.</summary>
    public bool ScanBots { get; set; }

    public ProvenanceOptions Provenance { get; set; } = new();
}
