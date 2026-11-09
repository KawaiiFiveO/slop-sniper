using System.Globalization;
using System.Text;

namespace SlopSniper.Activity;

/// <summary>Writes the private reply for the Activity profile command.</summary>
public static class ActivityFormatter
{
    // Below this, a regular's share would mostly be noise.
    private const int MinRegularTotal = 5;

    // Below this many replies the member's own numbers are too rough to read much into.
    private const int MinResponses = 15;

    private const int LabelWidth = 24;
    private const int ValueWidth = 14;

    private static readonly (string Label, Func<ActivityProfile, int> Count, Func<ActivityProfile, int> Total)[] Rows =
    [
        ("Opened a conversation", p => p.Started, p => p.Sampled),
        ("Followed own post", p => p.FollowUps, p => p.Sampled),
        ("Long reply too fast", p => p.FastReplies, p => p.LongReplies),
        ("Opens with their name", p => p.Named, p => p.Responses),
        ("Reuses their words", p => p.Echoed, p => p.Responses),
        ("Ends with an emoji", p => p.Emoji, p => p.Responses),
        ("All three together", p => p.Formula, p => p.Responses),
        ("Edited posts", p => p.Edited, p => p.Posts),
    ];

    public static string Format(ulong userId, int lookbackDays, ActivityProfile profile, IReadOnlyList<ActivityProfile> regulars)
    {
        var reply = new StringBuilder()
            .Append("**Activity profile of <@").Append(userId).Append(">**\n")
            .Append("Read ").Append(profile.Posts).Append(" posts from the last ").Append(lookbackDays)
            .Append(" days and compared ").Append(profile.Sampled).Append(" with the message before them.\n```\n")
            .Append(string.Empty.PadRight(LabelWidth)).Append("This member".PadRight(ValueWidth)).Append("Regulars\n");

        foreach (var (label, count, total) in Rows)
        {
            var regular = ActivityAnalyzer.Median(regulars.Where(r => total(r) >= MinRegularTotal).Select(r => Share(count(r), total(r))));
            reply.Append(label.PadRight(LabelWidth))
                .Append(Describe(count(profile), total(profile)).PadRight(ValueWidth))
                .Append(regular is { } share ? Percent(share) : "-")
                .Append('\n');
        }

        var regularSpeed = ActivityAnalyzer.Median(regulars.Where(r => r.MedianCharsPerSecond is not null).Select(r => r.MedianCharsPerSecond!.Value));
        reply.Append("Median reply chars/sec".PadRight(LabelWidth))
            .Append(Speed(profile.MedianCharsPerSecond).PadRight(ValueWidth))
            .Append(Speed(regularSpeed))
            .Append("\n```\n");

        reply.Append(regulars.Count > 0
            ? $"Regulars: the median of {regulars.Count} {(regulars.Count == 1 ? "person" : "people")} this member talks to most.\n"
            : "No regulars to compare with: too few of these posts followed another member.\n");
        if (profile.Responses < MinResponses)
        {
            reply.Append("Only ").Append(profile.Responses).Append(" replies were found, so these numbers are rough.\n");
        }
        return reply.Append("This is evidence to weigh, not proof. Someone writing through a translator or ChatGPT can look the same.").ToString();
    }

    private static double Share(int count, int total) => (double)count / total;

    private static string Percent(double share) => share.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string Describe(int count, int total) => total == 0 ? "-" : $"{Percent(Share(count, total))} ({count}/{total})";

    private static string Speed(double? value) => value is { } speed ? speed.ToString("0.0", CultureInfo.InvariantCulture) : "-";
}
