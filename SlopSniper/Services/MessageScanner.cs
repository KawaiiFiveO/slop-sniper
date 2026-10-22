using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.State;

namespace SlopSniper.Services;

/// <summary>Hands new messages in GuildId to every enabled module that isn't paused. Runs on the gateway thread, so it
/// and the modules only filter; the modules do their work on their own queues.</summary>
public sealed class MessageScanner(
    DiscordSocketClient client,
    IEnumerable<IMessageModule> modules,
    StateStore state,
    IOptionsMonitor<BotOptions> options) : IHostedService
{
    private readonly IMessageModule[] _modules = modules.ToArray();

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
        if (_modules.Length == 0
            || message is not SocketUserMessage userMessage
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
        var paused = await state.ReadAsync(s => s.PausedModules.ToArray());
        foreach (var module in _modules.Where(m => !paused.Contains(m.Name)))
        {
            module.OnMessage(userMessage, channel, images);
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
