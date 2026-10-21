using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Provenance;

/// <summary>Reports automatic detections where ReportMode says. Only informs: per OpenAI's terms, a provenance result
/// never leads to the message being deleted or its author punished.</summary>
public sealed class ProvenanceReporter(Reporter reporter, IOptionsMonitor<BotOptions> options)
{
    public async Task ReportDetectionAsync(ScanJob job, IReadOnlyList<ImageCheck> detected)
    {
        var mode = options.CurrentValue.Provenance.ReportMode;
        if (mode is ReportMode.Private or ReportMode.Both)
        {
            await reporter.SendToLogChannelAsync(embed: ProvenanceFormatter.BuildDetectionEmbed(job, detected));
        }
        if (mode is ReportMode.Public or ReportMode.Both)
        {
            await reporter.ReplyAsync(job, ProvenanceFormatter.BuildPublicReply(detected));
        }
    }
}
