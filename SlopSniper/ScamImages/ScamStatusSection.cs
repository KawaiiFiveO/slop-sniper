using System.Text;
using SlopSniper.Services;

namespace SlopSniper.ScamImages;

/// <summary>The scam detection part of /sniper status.</summary>
public sealed class ScamStatusSection(ScamStats stats) : IStatusSection
{
    public void AppendStatus(StringBuilder status)
    {
        status
            .Append("**Scam images**\n")
            .Append("- ").Append(stats.Detections).Append(" detections, ").Append(stats.FollowUps).Append(" more copies after them\n")
            .Append("- ").Append(stats.PostsDeleted).Append(" posts deleted, ").Append(stats.Timeouts).Append(" timeouts, ")
            .Append(stats.Failures).Append(" with failed actions, ").Append(stats.Undone).Append(" undone\n");
        if (stats.Dropped > 0)
        {
            status.Append("- ").Append(stats.Dropped).Append(" dropped (queue full)\n");
        }
    }
}
