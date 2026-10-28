using System.Text;
using Discord;

namespace SlopSniper.Scams;

/// <summary>Builds the moderator alerts for scam detections and their buttons.</summary>
public static class ScamReportFormatter
{
    // Carries the bot's timeout too. Alerts posted before that have the legacy prefix.
    public const string UndoButtonPrefix = "scam-revert";
    public const string LegacyUndoButtonPrefix = "scam-undo";
    public const string ConfirmButtonPrefix = "scam-act";
    public const string DismissButtonPrefix = "scam-ignore";

    private const string UndoFooter =
        "Undo lifts the bot's timeout (or puts back a shorter one it replaced) and forgets images and domains. Deleted posts can't be restored.";

    // Enough to recognise the scam; embed fields hold up to 1024 characters.
    private const int MaxTextLength = 500;

    private static readonly Color ScamColor = new(0xE74C3C);
    private static readonly Color SuspiciousColor = new(0xE67E22);
    private static readonly Color ClosedColor = new(0x95A5A6);

    /// <param name="evidenceFile">The attached copy of one of the images, shown in the embed, or null.</param>
    public static Embed BuildAlert(
        ScamDetection detection, ScamOutcome outcome, ulong guildId, int burstWindowSeconds, string? evidenceFile)
    {
        var actions = outcome.Actions;
        return BuildEmbed(detection, guildId, burstWindowSeconds, evidenceFile, showJumpLink: actions.Deleted < detection.Posts.Count)
            .WithTitle(actions.Deleted > 0 ? "Scam post removed" : "Scam post detected")
            .WithColor(ScamColor)
            .AddField("Actions", FormatActions(outcome))
            .WithFooter(UndoFooter)
            .Build();
    }

    /// <summary>An alert that only asks the moderators, for a post that isn't certain enough to act on.</summary>
    public static Embed BuildSuspiciousAlert(ScamDetection detection, ulong guildId, int burstWindowSeconds, string? evidenceFile) =>
        BuildEmbed(detection, guildId, burstWindowSeconds, evidenceFile, showJumpLink: true)
            .WithTitle("Possible scam post")
            .WithColor(SuspiciousColor)
            .AddField("Actions", "None yet. Not a scam also forgets the known images and domains it matched.")
            .Build();

    /// <summary>The bot's timeout and the one it replaced go in the ID as Unix milliseconds, 0 for none.</summary>
    public static MessageComponent BuildUndoButton(ulong eventId, ulong authorId, ScamActions actions) =>
        new ComponentBuilder()
            .WithButton("Undo", $"{UndoButtonPrefix}:{eventId}:{authorId}:{ToUnixMs(actions.TimedOutUntil)}:{ToUnixMs(actions.PreviousTimeout)}",
                ButtonStyle.Secondary)
            .Build();

    /// <summary>The inverse of the timestamps in <see cref="BuildUndoButton"/>.</summary>
    public static DateTimeOffset? FromUnixMs(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static long ToUnixMs(DateTimeOffset? at) => at?.ToUnixTimeMilliseconds() ?? 0;

    public static MessageComponent BuildSuspiciousButtons(PostRef post, ulong authorId) =>
        new ComponentBuilder()
            .WithButton("Delete & time out", $"{ConfirmButtonPrefix}:{post.ChannelId}:{post.MessageId}:{authorId}", ButtonStyle.Danger)
            .WithButton("Not a scam", $"{DismissButtonPrefix}:{post.ChannelId}:{post.MessageId}", ButtonStyle.Secondary)
            .Build();

    /// <summary>A "possible scam" alert after a moderator confirmed it.</summary>
    public static Embed MarkConfirmed(IEmbed alert, string moderator, ScamOutcome outcome) =>
        WithoutActions(alert)
            .WithTitle(outcome.Actions.Deleted > 0 ? "Scam post removed" : "Scam post confirmed")
            .WithColor(ScamColor)
            .AddField("Actions", $"Confirmed by {moderator}.\n{FormatActions(outcome)}")
            .WithFooter(UndoFooter)
            .Build();

    /// <summary>A "possible scam" alert after a moderator said it isn't one.</summary>
    public static Embed MarkDismissed(IEmbed alert, string moderator, KnownCounts forgotten) =>
        WithoutActions(alert)
            .WithColor(ClosedColor)
            .AddField("Not a scam", $"Marked by {moderator}. {FormatForgotten(forgotten)}")
            .Build();

    /// <summary>An alert after a moderator pressed Undo.</summary>
    public static Embed MarkUndone(IEmbed alert, string moderator, TimeoutUndo timeout, KnownCounts forgotten) =>
        alert.ToEmbedBuilder()
            .WithColor(ClosedColor)
            .AddField("Undone", timeout.Result switch
            {
                TimeoutUndoResult.Lifted => $"By {moderator}. Timeout lifted.",
                TimeoutUndoResult.Restored => $"By {moderator}. Put back the earlier timeout the bot replaced, ending {FormatTime(timeout.Until)}.",
                TimeoutUndoResult.LeftInPlace => $"By {moderator}. The timeout was changed after the bot set it, so it was left as it is (ends {FormatTime(timeout.Until)}).",
                TimeoutUndoResult.Failed => $"By {moderator}, but the timeout couldn't be lifted: {timeout.Error}.",
                _ => $"By {moderator}. No timeout of the bot's to lift.",
            }
                + " The author's posts are treated normally again. " + FormatForgotten(forgotten))
            .Build();

    /// <summary>The alert with its "Actions" field removed, to be replaced.</summary>
    private static EmbedBuilder WithoutActions(IEmbed alert)
    {
        var embed = alert.ToEmbedBuilder();
        embed.Fields.RemoveAll(f => f.Name == "Actions");
        return embed;
    }

    private static EmbedBuilder BuildEmbed(
        ScamDetection detection, ulong guildId, int burstWindowSeconds, string? evidenceFile, bool showJumpLink)
    {
        var channels = detection.Posts.Select(p => p.ChannelId).Distinct().Select(id => $"<#{id}>");
        var description = new StringBuilder()
            .Append($"<@{detection.AuthorId}> (`{detection.AuthorId}`) in ").AppendJoin(' ', channels);
        if (showJumpLink)
        {
            var (channelId, messageId) = detection.Post;
            description.Append($"\n[Jump to message](https://discord.com/channels/{guildId}/{channelId}/{messageId})");
        }

        var embed = new EmbedBuilder()
            .WithDescription(description.ToString())
            .AddField("Signals", FormatSignals(detection.Signals, burstWindowSeconds))
            .WithTimestamp(detection.Timestamp);
        if (detection.Content.Text.Length > 0)
        {
            embed.AddField("Text", FormatText(detection.Content.Text));
        }
        if (evidenceFile is not null)
        {
            embed.WithImageUrl($"attachment://{evidenceFile}");
        }
        return embed;
    }

    private static string FormatSignals(ScamSignals signals, int burstWindowSeconds)
    {
        List<string> lines = [];
        if (signals.ReportedBy is not null)
        {
            lines.Add($"Reported by {signals.ReportedBy}");
        }
        if (signals.Channels > 1)
        {
            var repeated = signals.Repeated switch
            {
                Repeated.LinkText => "text and link",
                Repeated.Images | Repeated.LinkText => "images and text",
                _ => "images",
            };
            lines.Add($"Same {repeated} in {signals.Channels} channels within {burstWindowSeconds}s");
        }
        if (signals.HashMatches > 0)
        {
            lines.Add(signals.HashMatches == 1 ? "1 image looks like a known scam image" : $"{signals.HashMatches} images look like known scam images");
        }
        if (signals.DomainMatches > 0)
        {
            lines.Add(signals.DomainMatches == 1 ? "Links to a known scam domain" : $"Links to {signals.DomainMatches} known scam domains");
        }
        if (signals.Words != ScamWords.None)
        {
            var keywords = signals.Keywords is { Count: > 0 } found ? $": {string.Join(", ", found)}" : "";
            lines.Add($"Scam words in the images ({ScamKeywords.Describe(signals.Words)}){keywords}");
        }
        if (signals.Shape)
        {
            lines.Add($"{signals.ImageCount} images, little text");
        }
        return lines.Count > 0 ? string.Join('\n', lines) : "None";
    }

    private static string FormatActions(ScamOutcome outcome)
    {
        var actions = outcome.Actions;
        List<string> lines = [];
        if (actions.ToDelete == 0)
        {
            lines.Add("Posts not deleted (DeleteMessages is off).");
        }
        else
        {
            lines.Add($"Deleted {actions.Deleted} of {actions.ToDelete} posts.");
            if (actions.DeleteError is not null)
            {
                lines.Add($"Couldn't delete the rest: {actions.DeleteError}.");
            }
        }

        if (actions.TimedOutFor is { } duration)
        {
            lines.Add(actions.PreviousTimeout is { } replaced
                ? $"Timed out for {FormatDuration(duration)}, replacing a shorter timeout ending {FormatTime(replaced)}."
                : $"Timed out for {FormatDuration(duration)}.");
        }
        else if (actions.TimeoutError is not null)
        {
            lines.Add($"Couldn't time out: {actions.TimeoutError}.");
        }
        else if (actions.PreviousTimeout is { } longer)
        {
            lines.Add($"Already timed out until {FormatTime(longer)}, longer than TimeoutMinutes, so left as it was.");
        }
        else
        {
            lines.Add("Not timed out (TimeoutMinutes is 0).");
        }

        if (outcome.Learned.Total > 0)
        {
            lines.Add($"Learned {DescribeLearned(outcome.Learned)}.");
        }

        return string.Join('\n', lines);
    }

    /// <summary>"1 new scam image and 2 new scam domains", or "nothing new".</summary>
    public static string DescribeLearned(KnownCounts learned) =>
        learned.Total == 0 ? "nothing new" : DescribeCounts(learned, "new scam");

    private static string FormatForgotten(KnownCounts forgotten) =>
        forgotten.Total == 0 ? "No known scam images or domains to forget." : $"Forgot {DescribeCounts(forgotten, "known scam")}.";

    private static string DescribeCounts(KnownCounts counts, string adjective)
    {
        List<string> parts = [];
        if (counts.Images > 0)
        {
            parts.Add(counts.Images == 1 ? $"1 {adjective} image" : $"{counts.Images} {adjective} images");
        }
        if (counts.Domains > 0)
        {
            parts.Add(counts.Domains == 1 ? $"1 {adjective} domain" : $"{counts.Domains} {adjective} domains");
        }
        return string.Join(" and ", parts);
    }

    /// <summary>In a code block, so links in it can't be clicked by accident.</summary>
    private static string FormatText(string text)
    {
        var shown = text.Length <= MaxTextLength ? text : text[..(MaxTextLength - 1)] + "…";
        return $"```\n{shown.Replace('`', '\'')}\n```";
    }

    private static string FormatTime(DateTimeOffset? at) =>
        at is { } value ? TimestampTag.FormatFromDateTimeOffset(value, TimestampTagStyles.Relative) : "at an unknown time";

    private static string FormatDuration(TimeSpan duration)
    {
        List<string> parts = [];
        if (duration.Days > 0)
        {
            parts.Add(duration.Days == 1 ? "1 day" : $"{duration.Days} days");
        }
        if (duration.Hours > 0)
        {
            parts.Add($"{duration.Hours} h");
        }
        if (duration.Minutes > 0)
        {
            parts.Add($"{duration.Minutes} min");
        }
        return string.Join(' ', parts);
    }
}
