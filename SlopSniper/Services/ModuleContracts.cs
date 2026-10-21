using System.Text;
using Discord.WebSocket;

namespace SlopSniper.Services;

/// <summary>A module that looks at new messages in GuildId. <see cref="MessageScanner"/> applies the shared filters
/// (excluded channels, bots, the paused flag) first.</summary>
public interface IMessageModule
{
    /// <summary>Runs on the gateway thread, so it may only filter and enqueue, never wait on the network.</summary>
    void OnMessage(SocketUserMessage message, SocketGuildChannel channel, IReadOnlyList<ImageSource> images);
}

/// <summary>A module's part of /sniper status.</summary>
public interface IStatusSection
{
    void AppendStatus(StringBuilder status);
}

/// <summary>An interaction module a feature adds. Global ones are registered everywhere and can be installed as a user
/// app; the rest only in GuildId.</summary>
public sealed record CommandModule(Type Type, bool Global);
