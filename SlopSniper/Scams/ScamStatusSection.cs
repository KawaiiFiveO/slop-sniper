using System.Text;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>The scam detection part of /sniper status.</summary>
public sealed class ScamStatusSection(ScamStats stats, KnownScams known) : IStatusSection
{
    public void AppendStatus(StringBuilder status)
    {
        status
            .Append("**Scam detection**\n")
            .Append("Known scam images: ").Append(known.ImageCount).Append(" · domains: ").Append(known.DomainCount).Append('\n')
            .Append("- ").Append(stats.Detections).Append(" detections, ").Append(stats.FollowUps).Append(" more copies after them, ")
            .Append(stats.SuspiciousAlerts).Append(" possible scams asked about\n")
            .Append("- ").Append(stats.PostsDeleted).Append(" posts deleted, ").Append(stats.Timeouts).Append(" timeouts, ")
            .Append(stats.Failures).Append(" with failed actions, ").Append(stats.Undone).Append(" undone\n");
        if (stats.Dropped > 0)
        {
            status.Append("- ").Append(stats.Dropped).Append(" dropped (queue full)\n");
        }
    }
}
