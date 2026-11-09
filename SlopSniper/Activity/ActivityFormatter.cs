using System.Globalization;
using System.Text;
using Discord;

namespace SlopSniper.Activity;

/// <summary>Writes the private reply for the Activity profile command.</summary>
public static class ActivityFormatter
{
    // Below this, a regular's share would mostly be noise.
    private const int MinRegularTotal = 5;

    // Below this many replies the member's own numbers are too rough to read much into.
    private const int MinResponses = 15;

    private const int LabelWidth = 24;
    private const int ColumnWidth = 15;
    private const int DateWidth = 12;
    private const int TimelineWidth = 8;

    private static readonly (string Label, Func<ActivityProfile, int> Count, Func<ActivityProfile, int> Total)[] Rows =
    [
        ("Opened a conversation", p => p.Started, p => p.Sampled),
        ("Followed own post", p => p.FollowUps, p => p.Sampled),
        ("Used the reply feature", p => p.UsedReplies, p => p.Sampled),
        ("Broken @mentions", p => p.BrokenMentions, p => p.Sampled),
        ("Long reply too fast", p => p.FastReplies, p => p.LongReplies),
        ("Opens with a name", p => p.Named, p => p.Addressable),
        ("Reuses their words", p => p.Echoed, p => p.Addressable),
        ("Ends with an emoji", p => p.Emoji, p => p.Addressable),
        ("Name and emoji", p => p.NameAndEmoji, p => p.Addressable),
        ("All three together", p => p.Formula, p => p.Addressable),
        ("Edited posts", p => p.Edited, p => p.Posts),
    ];

    private static readonly (string Label, Func<ActivityProfile, double?> Value)[] NumberRows =
    [
        ("Median reply chars/sec", p => p.MedianCharsPerSecond),
        ("Active hours (of 24)", p => p.ActiveHours),
        ("Longest quiet run (h)", p => p.QuietHours),
        ("Busy-day break (h)", p => p.BusyDayBreakHours),
    ];

    public static string Format(ulong userId, int postsCounted, bool incomplete, MemberProfile member, IReadOnlyList<ActivityProfile> regulars)
    {
        var all = member.All;
        var reply = new StringBuilder()
            .Append("**Activity profile of <@").Append(userId).Append(">**\n")
            .Append("Read ").Append(all.Posts).Append(" of ").Append(Math.Max(postsCounted, all.Posts))
            .Append(" posts and compared ").Append(all.Sampled).Append(" with the chat before them. Recent is their newest third, since ")
            .Append(TimestampTag.FormatFromDateTimeOffset(member.RecentFrom, TimestampTagStyles.ShortDate)).Append(".\n```\n")
            .Append(string.Empty.PadRight(LabelWidth))
            .Append("Recent".PadRight(ColumnWidth))
            .Append("Older".PadRight(ColumnWidth))
            .Append("Regulars\n");

        foreach (var (label, count, total) in Rows)
        {
            var regular = ActivityAnalyzer.Median(regulars.Where(r => total(r) >= MinRegularTotal).Select(r => Share(count(r), total(r))));
            reply.Append(label.PadRight(LabelWidth))
                .Append(Describe(count(member.Recent), total(member.Recent)).PadRight(ColumnWidth))
                .Append(Describe(count(member.Older), total(member.Older)).PadRight(ColumnWidth))
                .Append(regular is { } share ? Percent(share) : "-")
                .Append('\n');
        }

        foreach (var (label, value) in NumberRows)
        {
            reply.Append(label.PadRight(LabelWidth))
                .Append(Number(value(member.Recent)).PadRight(ColumnWidth))
                .Append(Number(value(member.Older)).PadRight(ColumnWidth))
                .Append(Number(ActivityAnalyzer.Median(regulars.Select(value).OfType<double>())))
                .Append('\n');
        }
        reply.Append("```\n");

        AppendTimeline(reply, member.Timeline);

        reply.Append(regulars.Count > 0
            ? $"Regulars: the median of {regulars.Count} {(regulars.Count == 1 ? "person" : "people")} this member talks to most.\n"
            : "No regulars to compare with: too few of these posts followed another member.\n");
        if (incomplete)
        {
            reply.Append("Discord didn't return all the posts it counted. A later run may get more.\n");
        }
        if (all.Responses < MinResponses)
        {
            reply.Append("Only ").Append(all.Responses).Append(" replies were found, so these numbers are rough.\n");
        }
        return reply.ToString().TrimEnd();
    }

    private static void AppendTimeline(StringBuilder reply, IReadOnlyList<ActivityPeriod> timeline)
    {
        if (timeline.Count == 0)
        {
            return;
        }

        reply.Append("Over time, oldest first:\n```\n")
            .Append("From".PadRight(DateWidth))
            .Append("Name".PadRight(TimelineWidth))
            .Append("Emoji".PadRight(TimelineWidth))
            .Append("Broken@".PadRight(TimelineWidth))
            .Append("Reply\n");

        foreach (var (from, profile) in timeline)
        {
            reply.Append(from.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).PadRight(DateWidth))
                .Append(PercentOf(profile.Named, profile.Addressable).PadRight(TimelineWidth))
                .Append(PercentOf(profile.Emoji, profile.Addressable).PadRight(TimelineWidth))
                .Append(PercentOf(profile.BrokenMentions, profile.Sampled).PadRight(TimelineWidth))
                .Append(PercentOf(profile.UsedReplies, profile.Sampled))
                .Append('\n');
        }
        reply.Append("```\n");
    }

    private static double Share(int count, int total) => (double)count / total;

    private static string Percent(double share) => share.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string PercentOf(int count, int total) => total == 0 ? "-" : Percent(Share(count, total));

    private static string Describe(int count, int total) => total == 0 ? "-" : $"{Percent(Share(count, total))} ({count}/{total})";

    private static string Number(double? value) => value is { } number ? number.ToString("0.#", CultureInfo.InvariantCulture) : "-";
}
