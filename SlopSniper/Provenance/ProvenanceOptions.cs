namespace SlopSniper.Provenance;

/// <summary>Where detections are reported.</summary>
public enum ReportMode
{
    /// <summary>An embed in the log channel.</summary>
    Private,

    /// <summary>A reply under the image.</summary>
    Public,

    Both,
}

/// <summary>The "Provenance" section: OpenAI provenance checks, automatic in GuildId and manual anywhere.</summary>
public sealed class ProvenanceOptions
{
    // The API rejects files over 50 MiB.
    private const int ApiMaxImageMiB = 50;

    private const int MaxQueueCapacity = 1000;

    /// <summary>Only read at startup, since it decides which services and commands exist.</summary>
    public bool Enabled { get; set; }

    /// <summary>OpenAI API key. Only needs the Images permission.</summary>
    public string OpenAiApiKey { get; set; } = "";

    /// <summary>Users who can run the manual check anywhere, in addition to the bot owner.</summary>
    public List<ulong> UserAppUserIds { get; set; } = [];

    public ReportMode ReportMode { get; set; } = ReportMode.Private;

    /// <summary>Larger attachments are skipped without being downloaded.</summary>
    public int MaxImageMiB { get; set; } = 20;

    /// <summary>Minimum gap between OpenAI requests.</summary>
    public int RequestIntervalSeconds { get; set; } = 3;

    /// <summary>Messages waiting to be checked before new ones are dropped.</summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>How long a result is reused for an identical file. 0 turns the cache off.</summary>
    public int CacheHours { get; set; } = 24;

    // Methods rather than properties, so ConfigReloader doesn't report them as separate settings.
    public long GetMaxImageBytes() => MaxImageMiB * 1024L * 1024L;

    public void Validate(List<string> errors)
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OpenAiApiKey))
        {
            errors.Add("Provenance.OpenAiApiKey is required.");
        }

        if (!Enum.IsDefined(ReportMode))
        {
            errors.Add("Provenance.ReportMode must be Private, Public or Both.");
        }

        if (MaxImageMiB is < 1 or > ApiMaxImageMiB)
        {
            errors.Add($"Provenance.MaxImageMiB must be between 1 and {ApiMaxImageMiB}.");
        }

        if (RequestIntervalSeconds < 1)
        {
            errors.Add("Provenance.RequestIntervalSeconds must be at least 1.");
        }

        if (QueueCapacity is < 1 or > MaxQueueCapacity)
        {
            errors.Add($"Provenance.QueueCapacity must be between 1 and {MaxQueueCapacity}.");
        }

        if (CacheHours < 0)
        {
            errors.Add("Provenance.CacheHours can't be negative.");
        }
    }
}
