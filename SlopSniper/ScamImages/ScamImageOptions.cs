namespace SlopSniper.ScamImages;

/// <summary>The "ScamImages" section: catches hijacked accounts posting scam screenshots (fake crypto giveaways,
/// casino "withdrawals") across channels. Unlike provenance checks, this module deletes posts and times out the
/// author.</summary>
public sealed class ScamImageOptions
{
    // Discord's longest timeout.
    private const int MaxTimeoutMinutes = 28 * 24 * 60;

    /// <summary>Only read at startup, since it decides which services and commands exist. Needs GuildId.</summary>
    public bool Enabled { get; set; }

    /// <summary>How long after a post the same images in another channel count as a burst.</summary>
    public int BurstWindowSeconds { get; set; } = 60;

    /// <summary>Channels the same images must be posted to before the bot acts on that alone.</summary>
    public int BurstChannels { get; set; } = 3;

    /// <summary>A post with at least this many images and little text looks like a scam post. Not acted on alone.</summary>
    public int ShapeMinImages { get; set; } = 3;

    public bool DeleteMessages { get; set; } = true;

    /// <summary>How long the author is timed out. 0 doesn't time them out.</summary>
    public int TimeoutMinutes { get; set; } = 1440;

    /// <summary>Members with any of these roles are never acted on. Members who can manage messages or moderate
    /// members never are either.</summary>
    public List<ulong> ExemptRoleIds { get; set; } = [];

    public void Validate(List<string> errors, ulong guildId)
    {
        if (!Enabled)
        {
            return;
        }

        if (guildId == 0)
        {
            errors.Add("ScamImages needs GuildId: it only scans that server.");
        }

        if (BurstWindowSeconds is < 10 or > 600)
        {
            errors.Add("ScamImages.BurstWindowSeconds must be between 10 and 600.");
        }

        if (BurstChannels is < 2 or > 20)
        {
            errors.Add("ScamImages.BurstChannels must be between 2 and 20.");
        }

        if (ShapeMinImages is < 1 or > 10)
        {
            errors.Add("ScamImages.ShapeMinImages must be between 1 and 10.");
        }

        if (TimeoutMinutes is < 0 or > MaxTimeoutMinutes)
        {
            errors.Add($"ScamImages.TimeoutMinutes must be between 0 and {MaxTimeoutMinutes} (28 days).");
        }
    }
}
