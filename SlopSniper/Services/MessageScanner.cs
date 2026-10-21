using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.State;

namespace SlopSniper.Services;

/// <summary>Queues new messages with images for checking. Runs on the gateway thread, so it only filters and
/// enqueues; the checks happen in <see cref="ScanQueue"/>.</summary>
public sealed class MessageScanner(
    DiscordSocketClient client,
    ScanQueue queue,
    ScanStats stats,
    StateStore state,
    IOptionsMonitor<BotOptions> options,
    ILogger<MessageScanner> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        client.MessageReceived += OnMessageReceivedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        client.MessageReceived -= OnMessageReceivedAsync;
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        var settings = options.CurrentValue;
        if (message is not SocketUserMessage
            || message.Channel is not SocketGuildChannel channel
            || channel.Guild.Id != settings.GuildId
            || message.Author.Id == client.CurrentUser.Id
            || (message.Author.IsBot && !message.Author.IsWebhook && !settings.ScanBots)
            || IsExcluded(channel, settings.ExcludedChannelIds))
        {
            return;
        }

        // Link previews are only checked manually.
        var images = ImageFormats.GetImages(message, includeLinkPreviews: false);
        if (images.Count == 0 || await state.ReadAsync(s => s.ScanningPaused))
        {
            return;
        }

        if (queue.TryEnqueue(ScanJob.Create(message, channel.Guild.Id, channel.Id, images)))
        {
            stats.MessageQueued();
        }
        else
        {
            stats.MessageDropped();
            logger.LogWarning("Scan queue is full; skipped message {MessageId} in {ChannelId}.", message.Id, channel.Id);
        }
    }

    /// <summary>Matches the channel itself, or for a thread its parent channel, and the category either is in.</summary>
    private static bool IsExcluded(SocketGuildChannel channel, List<ulong> excluded)
    {
        if (excluded.Count == 0)
        {
            return false;
        }

        var parent = channel is SocketThreadChannel { ParentChannel: { } threadParent } ? threadParent : channel;
        return excluded.Contains(channel.Id)
            || excluded.Contains(parent.Id)
            || (parent is INestedChannel { CategoryId: { } categoryId } && excluded.Contains(categoryId));
    }
}
