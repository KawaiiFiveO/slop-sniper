using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Provenance;

/// <summary>Queues every new message with images in GuildId for an automatic check.</summary>
public sealed class ProvenanceScanner(ProvenanceQueue queue, ProvenanceStats stats, ILogger<ProvenanceScanner> logger)
    : IMessageModule
{
    public string Name => nameof(BotOptions.Provenance);

    public string DisplayName => "provenance checks";

    public void OnMessage(SocketUserMessage message, SocketGuildChannel channel, IReadOnlyList<ImageSource> images)
    {
        if (queue.TryEnqueue(ScanJob.Create(message, channel.Guild.Id, channel.Id, images)))
        {
            stats.MessageQueued();
        }
        else
        {
            stats.MessageDropped();
            logger.LogWarning("Provenance queue is full; skipped message {MessageId} in {ChannelId}.", message.Id, channel.Id);
        }
    }
}
