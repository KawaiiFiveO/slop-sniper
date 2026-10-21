using System.Globalization;
using Discord;
using SlopSniper.Provenance;

namespace SlopSniper.Services;

/// <summary>Builds the report embeds and the public reply text.</summary>
public static class ReportFormatter
{
    private static readonly Color DetectedColor = new(0xE67E22);
    private static readonly Color ClearColor = new(0x95A5A6);

    private const string DetectedFooter = "Detected content was generated using OpenAI tools.";

    private const string NotDetectedFooter = "Not proof it's human-made. Other AI providers aren't detected.";

    // Well under Discord's 256-character field name limit, so ten images stay within the 6000-character embed limit.
    private const int MaxFieldNameLength = 100;

    /// <summary>The private report for an automatic scan, sent to the log channel.</summary>
    public static Embed BuildDetectionEmbed(ScanJob job, IReadOnlyList<ImageCheck> detected)
    {
        var embed = new EmbedBuilder()
            .WithTitle("OpenAI provenance detected")
            .WithColor(DetectedColor)
            .WithDescription(DescribeMessage(job))
            .WithTimestamp(job.Timestamp)
            .WithFooter(DetectedFooter);

        foreach (var check in detected)
        {
            embed.AddField(FieldName(check.Image), FormatSignals(check.Result!, detectedOnly: true));
        }

        // Spoilered images aren't shown as thumbnails.
        if (detected.FirstOrDefault(c => !c.Image.IsSpoiler) is { } thumbnail)
        {
            embed.WithThumbnailUrl(thumbnail.Image.Url);
        }

        return embed.Build();
    }

    /// <summary>The reply to a manual check, which also covers images that weren't detected or checked.</summary>
    /// <param name="notChecked">Images left out because the message had more than one check covers.</param>
    public static Embed BuildManualEmbed(ScanJob job, IReadOnlyList<ImageCheck> checks, int maxImageMiB, int notChecked)
    {
        var detected = checks.Any(c => c.Detected);
        var description = DescribeMessage(job);
        if (notChecked > 0)
        {
            description += $"\n{notChecked} more {(notChecked == 1 ? "image wasn't" : "images weren't")} checked.";
        }

        var embed = new EmbedBuilder()
            .WithTitle(detected ? "OpenAI provenance detected" : "No OpenAI provenance found")
            .WithColor(detected ? DetectedColor : ClearColor)
            .WithDescription(description)
            .WithFooter(detected ? DetectedFooter : NotDetectedFooter);

        foreach (var check in checks)
        {
            var value = check.Status switch
            {
                CheckStatus.Checked => FormatSignals(check.Result!, detectedOnly: false),
                CheckStatus.TooLarge => $"Not checked: too large ({check.Detail}, limit {maxImageMiB} MiB).",
                CheckStatus.Unsupported => $"Not checked: unsupported type ({check.Detail}).",
                _ => $"Check failed: {check.Detail}",
            };
            embed.AddField(FieldName(check.Image), Truncate(value, EmbedFieldBuilder.MaxFieldValueLength));
        }

        return embed.Build();
    }

    public static string BuildPublicReply(IEnumerable<ImageCheck> detected)
    {
        var signals = detected
            .SelectMany(c => c.Result!.Results)
            .Where(s => s.Detected)
            .Select(s => SignalName(s.Type))
            .Distinct();
        return $"An image in this message contains OpenAI provenance signals ({string.Join(", ", signals)}).";
    }

    // DMs have no channel to mention, only the jump link.
    private static string DescribeMessage(ScanJob job) =>
        (job.GuildId is null ? $"<@{job.AuthorId}> in a DM" : $"<@{job.AuthorId}> in <#{job.ChannelId}>")
        + $"\n[Jump to message]({job.JumpUrl})";

    private static string FormatSignals(ProvenanceCheck result, bool detectedOnly)
    {
        var lines = result.Results
            .Where(s => s.Detected || !detectedOnly)
            .Select(FormatSignal)
            .ToList();
        if (lines.Count == 0)
        {
            return result.Results.Count == 0 ? "No provenance checks applied to this file." : "No OpenAI signals found.";
        }
        return string.Join('\n', lines);
    }

    private static string FormatSignal(ProvenanceSignal signal)
    {
        if (!signal.Detected)
        {
            return $"{SignalName(signal.Type)}: not detected";
        }

        var headline = $"{SignalName(signal.Type)}: detected";
        if (signal.ValidationState is { } validation)
        {
            headline += $" ({validation})";
        }

        List<string> parts = [headline];
        if (signal.Model is { } model)
        {
            parts.Add(model);
        }
        if (DateTimeOffset.TryParse(signal.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var generated))
        {
            parts.Add(generated.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        return string.Join(" · ", parts);
    }

    private static string SignalName(string type) => type switch
    {
        "c2pa" => "C2PA",
        "synthid" => "SynthID",
        _ => type,
    };

    private static string FieldName(ImageSource image) => Truncate(image.Name, MaxFieldNameLength);

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
}
