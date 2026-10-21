using Discord;

namespace SlopSniper.Services;

/// <summary>A message whose images are being checked.</summary>
/// <param name="GuildId">Null in DMs and group DMs.</param>
public sealed record ScanJob(
    ulong? GuildId,
    ulong ChannelId,
    ulong MessageId,
    ulong AuthorId,
    DateTimeOffset Timestamp,
    IReadOnlyList<ImageSource> Images)
{
    public string JumpUrl => $"https://discord.com/channels/{GuildId?.ToString() ?? "@me"}/{ChannelId}/{MessageId}";

    // Built from IDs rather than message.Channel, which isn't always populated on messages from interactions.
    public static ScanJob Create(IMessage message, ulong? guildId, ulong channelId, IReadOnlyList<ImageSource> images) =>
        new(guildId, channelId, message.Id, message.Author.Id, message.Timestamp, images);
}
