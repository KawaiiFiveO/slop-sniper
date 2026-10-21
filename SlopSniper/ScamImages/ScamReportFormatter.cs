using System.Text;
using Discord;

namespace SlopSniper.ScamImages;

/// <summary>Builds the moderator alert for a scam detection and its Undo button.</summary>
public static class ScamReportFormatter
{
    public const string UndoButtonPrefix = "scam-undo";

    private static readonly Color ScamColor = new(0xE74C3C);
    private static readonly Color UndoneColor = new(0x95A5A6);

    /// <param name="evidenceFile">The attached copy of one of the images, shown in the embed, or null.</param>
    public static Embed BuildAlert(
        ScamDetection detection, ScamActions actions, ulong guildId, int burstWindowSeconds, string? evidenceFile)
    {
        var channels = detection.Posts.Select(p => p.ChannelId).Distinct().Select(id => $"<#{id}>");
        var description = new StringBuilder()
            .Append($"<@{detection.AuthorId}> (`{detection.AuthorId}`) in ").AppendJoin(' ', channels);
        if (actions.Deleted < detection.Posts.Count)
        {
            var (channelId, messageId) = detection.Post;
            description.Append($"\n[Jump to message](https://discord.com/channels/{guildId}/{channelId}/{messageId})");
        }

        List<string> signals = [];
        if (detection.Signals.Channels > 1)
        {
            signals.Add($"Same images in {detection.Signals.Channels} channels within {burstWindowSeconds}s");
        }
        if (detection.Signals.Shape)
        {
            signals.Add($"{detection.Signals.ImageCount} images, little text");
        }

        var embed = new EmbedBuilder()
            .WithTitle(actions.Deleted > 0 ? "Scam images removed" : "Scam images detected")
            .WithColor(ScamColor)
            .WithDescription(description.ToString())
            .AddField("Signals", string.Join('\n', signals))
            .AddField("Actions", FormatActions(actions))
            .WithTimestamp(detection.Timestamp)
            .WithFooter("Undo lifts the timeout. Deleted posts can't be restored.");
        if (evidenceFile is not null)
        {
            embed.WithImageUrl($"attachment://{evidenceFile}");
        }
        return embed.Build();
    }

    public static MessageComponent BuildUndoButton(ulong eventId, ulong authorId) =>
        new ComponentBuilder()
            .WithButton("Undo", $"{UndoButtonPrefix}:{eventId}:{authorId}", ButtonStyle.Secondary)
            .Build();

    /// <summary>The alert after a moderator pressed Undo.</summary>
    public static Embed MarkUndone(IEmbed alert, string moderator, string? timeoutError) =>
        alert.ToEmbedBuilder()
            .WithColor(UndoneColor)
            .AddField("Undone", timeoutError is null
                ? $"By {moderator}. Timeout lifted; the author's posts are treated normally again."
                : $"By {moderator}, but the timeout couldn't be lifted: {timeoutError}")
            .Build();

    private static string FormatActions(ScamActions actions)
    {
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
            lines.Add($"Timed out for {FormatDuration(duration)}.");
        }
        else if (actions.TimeoutError is not null)
        {
            lines.Add($"Couldn't time out: {actions.TimeoutError}.");
        }
        else
        {
            lines.Add("Not timed out (TimeoutMinutes is 0).");
        }

        return string.Join('\n', lines);
    }

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
