using Discord;

namespace SlopSniper.Services;

/// <summary>A message whose images are waiting to be checked.</summary>
public sealed record ScanJob(
    ulong ChannelId,
    ulong MessageId,
    ulong AuthorId,
    string JumpUrl,
    DateTimeOffset Timestamp,
    IReadOnlyList<IAttachment> Images)
{
    // Built from IDs rather than message.Channel, which isn't always populated on messages from interactions.
    public static ScanJob Create(IMessage message, ulong guildId, ulong channelId, IReadOnlyList<IAttachment> images) => new(
        channelId,
        message.Id,
        message.Author.Id,
        $"https://discord.com/channels/{guildId}/{channelId}/{message.Id}",
        message.Timestamp,
        images);
}
