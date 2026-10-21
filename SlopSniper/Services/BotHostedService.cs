using System.Net;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;

namespace SlopSniper.Services;

/// <summary>Owns the Discord connection: login, command registration and shutdown.</summary>
public sealed class BotHostedService(
    DiscordSocketClient client,
    InteractionService interactions,
    CommandHandler commandHandler,
    IOptions<BotOptions> options,
    IOptionsMonitor<BotOptions> currentOptions,
    IHostApplicationLifetime lifetime,
    ILoggerFactory loggerFactory,
    ILogger<BotHostedService> logger) : IHostedService
{
    // Gateway close code for intents that aren't enabled in the Developer Portal.
    private const int DisallowedIntentsCloseCode = 4014;

    private readonly ILogger _discordLogger = loggerFactory.CreateLogger("Discord");
    private bool _commandsRegistered;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        client.Log += LogDiscordAsync;
        interactions.Log += LogDiscordAsync;
        client.Ready += OnReadyAsync;
        client.Disconnected += OnDisconnectedAsync;

        // Modules must be loaded before Ready fires, or there is nothing to register.
        await commandHandler.InitializeAsync();

        await client.LoginAsync(TokenType.Bot, options.Value.Token);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Bot is shutting down.");
        await client.StopAsync();
        await client.LogoutAsync();
    }

    private async Task OnReadyAsync()
    {
        logger.LogInformation("Connected as {Username}.", client.CurrentUser.Username);

        var logChannelId = currentOptions.CurrentValue.LogChannelId;
        if (client.GetChannel(logChannelId) is not IMessageChannel)
        {
            logger.LogWarning("Log channel {ChannelId} wasn't found or isn't a text channel. Reports and alerts can't be sent.", logChannelId);
        }

        // Ready fires again after every reconnect; commands only need registering once per run.
        if (_commandsRegistered)
        {
            return;
        }

        // Commands are registered to the configured guild only, never globally.
        var guildId = options.Value.GuildId;
        await interactions.RegisterCommandsToGuildAsync(guildId, deleteMissing: true);
        _commandsRegistered = true;
        logger.LogInformation("Registered commands to guild {GuildId}.", guildId);
    }

    private Task OnDisconnectedAsync(Exception exception)
    {
        // Discord.Net retries these forever; stop instead so the bad config is visible.
        if (exception is Discord.Net.HttpException { HttpCode: HttpStatusCode.Unauthorized })
        {
            logger.LogCritical("Discord rejected the bot token. Check Token in config.json.");
            Environment.ExitCode = ExitCodes.ConfigError;
            lifetime.StopApplication();
        }
        else if ((exception as Discord.Net.WebSocketClosedException ?? exception.InnerException as Discord.Net.WebSocketClosedException)
            is { CloseCode: DisallowedIntentsCloseCode })
        {
            logger.LogCritical("Discord refused the Message Content intent. Enable it under Bot > Privileged Gateway Intents in the Developer Portal.");
            Environment.ExitCode = ExitCodes.ConfigError;
            lifetime.StopApplication();
        }
        return Task.CompletedTask;
    }

    private Task LogDiscordAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
        _discordLogger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
