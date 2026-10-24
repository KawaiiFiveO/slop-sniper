namespace SlopSniper.Scams;

/// <summary>The "Scams" section: catches hijacked accounts posting scam screenshots (fake crypto giveaways,
/// casino "withdrawals") or scam links across channels. Unlike provenance checks, this module deletes posts and times
/// out the author.</summary>
public sealed class ScamOptions
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

    /// <summary>Two images whose perceptual hashes differ in at most this many of 64 bits count as the same image.</summary>
    public int HashDistance { get; set; } = 8;

    /// <summary>Known scam images remembered. Past this, the oldest are forgotten.</summary>
    public int MaxKnownImages { get; set; } = 500;

    /// <summary>Domains never learned as scam domains, with their subdomains. Adds to a built-in list of big sites.</summary>
    public List<string> TrustedDomains { get; set; } = [];

    /// <summary>Tesseract, for reading the text in suspicious images. Empty turns OCR off; if it isn't installed, the
    /// bot works without it.</summary>
    public string OcrCommand { get; set; } = "tesseract";

    /// <summary>Read the text in every image post, not only ones that already look suspicious. Slower, and on its own
    /// it only ever raises a "possible scam" alert.</summary>
    public bool OcrAllImages { get; set; }

    /// <summary>The words OCR looks for in images.</summary>
    public ScamWordOptions Words { get; set; } = new();

    public void Validate(List<string> errors, ulong guildId)
    {
        if (!Enabled)
        {
            return;
        }

        if (guildId == 0)
        {
            errors.Add("Scams needs GuildId: it only scans that server.");
        }

        if (BurstWindowSeconds is < 10 or > 600)
        {
            errors.Add("Scams.BurstWindowSeconds must be between 10 and 600.");
        }

        if (BurstChannels is < 2 or > 20)
        {
            errors.Add("Scams.BurstChannels must be between 2 and 20.");
        }

        if (ShapeMinImages is < 1 or > 10)
        {
            errors.Add("Scams.ShapeMinImages must be between 1 and 10.");
        }

        if (HashDistance is < 0 or > 16)
        {
            errors.Add("Scams.HashDistance must be between 0 and 16.");
        }

        if (MaxKnownImages is < 10 or > 5000)
        {
            errors.Add("Scams.MaxKnownImages must be between 10 and 5000.");
        }

        if (TimeoutMinutes is < 0 or > MaxTimeoutMinutes)
        {
            errors.Add($"Scams.TimeoutMinutes must be between 0 and {MaxTimeoutMinutes} (28 days).");
        }

        foreach (var domain in TrustedDomains)
        {
            var normalized = ScamLinks.Normalize(domain);
            if (!normalized.Contains('.') || Uri.CheckHostName(normalized) != UriHostNameType.Dns)
            {
                errors.Add($"Scams.TrustedDomains: \"{domain}\" isn't a domain like example.com.");
            }
        }

        Words.Validate(errors);
    }
}
