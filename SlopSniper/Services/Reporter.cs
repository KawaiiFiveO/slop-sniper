using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;

namespace SlopSniper.Services;

/// <summary>Sends detection reports and service alerts to Discord.</summary>
public sealed class Reporter(DiscordSocketClient client, IOptionsMonitor<BotOptions> options, ILogger<Reporter> logger)
{
    // Replies never ping the image's author.
    private static readonly AllowedMentions NoMentions = new(AllowedMentionTypes.None) { MentionRepliedUser = false };

    public async Task ReportDetectionAsync(ScanJob job, IReadOnlyList<ImageCheck> detected)
    {
        var mode = options.CurrentValue.ReportMode;
        if (mode is ReportMode.Private or ReportMode.Both)
        {
            await SendToLogChannelAsync(embed: ReportFormatter.BuildDetectionEmbed(job, detected));
        }
        if (mode is ReportMode.Public or ReportMode.Both)
        {
            await ReplyAsync(job, ReportFormatter.BuildPublicReply(detected));
        }
    }

    /// <summary>Posts a notice for moderators, such as OpenAI checks being suspended.</summary>
    public Task SendAlertAsync(string text) => SendToLogChannelAsync(text: text);

    private async Task SendToLogChannelAsync(string? text = null, Embed? embed = null)
    {
        // No log channel in user-app-only mode (GuildId 0); results are only shown to whoever ran the check.
        var channelId = options.CurrentValue.LogChannelId;
        if (channelId == 0)
        {
            return;
        }
        if (client.GetChannel(channelId) is not IMessageChannel channel)
        {
            logger.LogWarning("Log channel {ChannelId} wasn't found or isn't a text channel.", channelId);
            return;
        }

        try
        {
            await channel.SendMessageAsync(text, embed: embed, allowedMentions: NoMentions);
        }
        catch (Exception ex) when (ex is Discord.Net.HttpException or TimeoutException)
        {
            logger.LogWarning("Couldn't send to log channel {ChannelId}: {Error}", channelId, ex.Message);
        }
    }

    private async Task ReplyAsync(ScanJob job, string text)
    {
        if (client.GetChannel(job.ChannelId) is not IMessageChannel channel)
        {
            return;
        }

        try
        {
            await channel.SendMessageAsync(
                text,
                allowedMentions: NoMentions,
                messageReference: new MessageReference(job.MessageId, failIfNotExists: false));
        }
        catch (Exception ex) when (ex is Discord.Net.HttpException or TimeoutException)
        {
            logger.LogWarning("Couldn't reply to message {MessageId} in {ChannelId}: {Error}", job.MessageId, job.ChannelId, ex.Message);
        }
    }
}
