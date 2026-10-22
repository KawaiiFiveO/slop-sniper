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
    TimeProvider time,
    IOptionsMonitor<BotOptions> options) : IHostedService
{
    // Discord adds link previews within seconds; later updates are edits, which aren't scanned.
    private static readonly TimeSpan MaxPreviewDelay = TimeSpan.FromMinutes(2);

    private readonly IMessageModule[] _modules = modules.ToArray();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        client.MessageReceived += OnMessageReceivedAsync;
        client.MessageUpdated += OnMessageUpdatedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        client.MessageReceived -= OnMessageReceivedAsync;
        client.MessageUpdated -= OnMessageUpdatedAsync;
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (!ShouldScan(message, out var userMessage, out var channel))
        {
            return;
        }

        // Link previews arrive later, as an update.
        var images = ImageFormats.GetImages(message, includeLinkPreviews: false);
        foreach (var module in await GetActiveModulesAsync())
        {
            module.OnMessage(userMessage, channel, images);
        }
    }

    private async Task OnMessageUpdatedAsync(Cacheable<IMessage, ulong> before, SocketMessage message, ISocketMessageChannel _)
    {
        if (message.Embeds.Count == 0
            || time.GetUtcNow() - message.Timestamp > MaxPreviewDelay
            || !ShouldScan(message, out var userMessage, out var channel))
        {
            return;
        }

        var previews = ImageFormats.GetImages(message, includeLinkPreviews: true).Where(i => i.AttachmentId is null).ToList();
        if (previews.Count == 0)
        {
            return;
        }

        foreach (var module in await GetActiveModulesAsync())
        {
            module.OnLinkPreviews(userMessage, channel, previews);
        }
    }

    private bool ShouldScan(SocketMessage message, out SocketUserMessage userMessage, out SocketGuildChannel channel)
    {
        var settings = options.CurrentValue;
        userMessage = null!;
        channel = null!;
        if (_modules.Length == 0
            || message is not SocketUserMessage user
            || message.Channel is not SocketGuildChannel guildChannel
            || guildChannel.Guild.Id != settings.GuildId
            || message.Author.Id == client.CurrentUser.Id
            || (message.Author.IsBot && !message.Author.IsWebhook && !settings.ScanBots)
            || IsExcluded(guildChannel, settings.ExcludedChannelIds))
        {
            return false;
        }

        (userMessage, channel) = (user, guildChannel);
        return true;
    }

    private async Task<IEnumerable<IMessageModule>> GetActiveModulesAsync()
    {
        var paused = await state.ReadAsync(s => s.PausedModules.ToArray());
        return _modules.Where(m => !paused.Contains(m.Name));
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
