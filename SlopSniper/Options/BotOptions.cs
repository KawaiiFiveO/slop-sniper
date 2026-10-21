namespace SlopSniper.Options;

/// <summary>Where detections are reported.</summary>
public enum ReportMode
{
    /// <summary>An embed in the log channel.</summary>
    Private,

    /// <summary>A reply under the image.</summary>
    Public,

    Both,
}

/// <summary>Settings bound from config.json (and SLOPSNIPER_* environment variables).</summary>
public sealed class BotOptions
{
    public const string FileName = "config.json";

    public string Token { get; set; } = "";

    /// <summary>The server that's scanned automatically and gets /sniper. 0 runs as a user app only.</summary>
    public ulong GuildId { get; set; }

    /// <summary>OpenAI API key. Only needs the Images permission.</summary>
    public string OpenAiApiKey { get; set; } = "";

    /// <summary>Moderator channel for private reports and OpenAI service alerts. Required when GuildId is set.</summary>
    public ulong LogChannelId { get; set; }

    /// <summary>Users who can run the manual check anywhere, in addition to the bot owner.</summary>
    public List<ulong> UserAppUserIds { get; set; } = [];

    public ReportMode ReportMode { get; set; } = ReportMode.Private;

    /// <summary>Channels or categories that aren't scanned automatically. Threads follow their parent channel.</summary>
    public List<ulong> ExcludedChannelIds { get; set; } = [];

    /// <summary>Also scan messages from other bots. Webhook messages (e.g. PluralKit) are always scanned.</summary>
    public bool ScanBots { get; set; }

    /// <summary>Larger attachments are skipped without being downloaded.</summary>
    public int MaxImageMiB { get; set; } = 20;

    /// <summary>Minimum gap between OpenAI requests.</summary>
    public int RequestIntervalSeconds { get; set; } = 3;

    /// <summary>Messages waiting to be checked before new ones are dropped.</summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>How long a result is reused for an identical file. 0 turns the cache off.</summary>
    public int CacheHours { get; set; } = 24;

    // A method rather than a property, so ConfigReloader doesn't report it as a separate setting.
    public long GetMaxImageBytes() => MaxImageMiB * 1024L * 1024L;
}
