using System.Text;
using Discord.WebSocket;

namespace SlopSniper.Services;

/// <summary>A module that looks at new messages in GuildId. <see cref="MessageScanner"/> applies the shared filters
/// (excluded channels, bots, pausing) first.</summary>
public interface IMessageModule
{
    /// <summary>The module's config section name, which /sniper pause uses too.</summary>
    string Name { get; }

    /// <summary>How the module is called in replies and alerts, e.g. "scam detection".</summary>
    string DisplayName { get; }

    /// <summary>Called for every message, with or without images. Runs on the gateway thread, so it may only filter
    /// and enqueue, never wait on the network.</summary>
    void OnMessage(SocketUserMessage message, SocketGuildChannel channel, IReadOnlyList<ImageSource> images);

    /// <summary>Called when Discord adds link previews with images to a recent message, which it does a moment after
    /// the message arrives. Also on the gateway thread. Ignored unless a module needs it.</summary>
    void OnLinkPreviews(SocketUserMessage message, SocketGuildChannel channel, IReadOnlyList<ImageSource> previews)
    {
    }
}

/// <summary>A module's part of /sniper status.</summary>
public interface IStatusSection
{
    void AppendStatus(StringBuilder status);
}

/// <summary>An interaction module a feature adds. Global ones are registered everywhere and can be installed as a user
/// app; the rest only in GuildId.</summary>
public sealed record CommandModule(Type Type, bool Global);
