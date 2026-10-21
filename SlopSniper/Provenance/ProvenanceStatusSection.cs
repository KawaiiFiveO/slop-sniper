using System.Text;
using Discord;
using SlopSniper.Services;

namespace SlopSniper.Provenance;

/// <summary>The provenance part of /sniper status.</summary>
public sealed class ProvenanceStatusSection(ProvenanceQueue queue, ProvenanceStats stats, OpenAiStatus openAi) : IStatusSection
{
    public void AppendStatus(StringBuilder status)
    {
        status
            .Append("**Provenance**\n")
            .Append("OpenAI: ").Append(FormatOpenAiStatus()).Append('\n')
            .Append("Queue: ").Append(queue.Count).Append(" waiting\n")
            .Append("Last OpenAI check: ").Append(openAi.LastSuccess is { } last ? Timestamp(last) : "none yet").Append('\n')
            .Append("- ").Append(stats.MessagesQueued).Append(" messages scanned, ").Append(stats.MessagesDropped).Append(" dropped (queue full)\n")
            .Append("- ").Append(stats.ImagesChecked).Append(" images checked (").Append(stats.CacheHits).Append(" from cache), ")
            .Append(stats.ImagesDetected).Append(" detected\n")
            .Append("- ").Append(stats.ImagesSkipped).Append(" skipped (too large or unsupported), ")
            .Append(stats.ImagesFailed).Append(" failed\n");
    }

    private string FormatOpenAiStatus()
    {
        if (openAi.SuspendedReason is not { } reason)
        {
            return "ok";
        }

        var retry = openAi.RetryAt is { } retryAt ? $", retrying {Timestamp(retryAt)}" : ", until `/sniper-reload` or a restart";
        return $"suspended{retry}\n> {reason}";
    }

    private static string Timestamp(DateTimeOffset time) => TimestampTag.FormatFromDateTimeOffset(time, TimestampTagStyles.Relative);
}
