using System.Net;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.State;

namespace SlopSniper.Services;

/// <summary>Owns the Discord connection: login, command registration and shutdown.</summary>
public sealed class BotHostedService(
    DiscordSocketClient client,
    InteractionService interactions,
    CommandHandler commandHandler,
    StateStore state,
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

        var guildId = options.Value.GuildId;
        var logChannelId = currentOptions.CurrentValue.LogChannelId;
        if (guildId != 0 && client.GetChannel(logChannelId) is not IMessageChannel)
        {
            logger.LogWarning("Log channel {ChannelId} wasn't found or isn't a text channel. Reports and alerts can't be sent.", logChannelId);
        }

        // Ready fires again after every reconnect; commands only need registering once per run.
        if (_commandsRegistered)
        {
            return;
        }

        // Global commands can be installed as a user app; the rest stay in the home server.
        await interactions.AddModulesGloballyAsync(deleteMissing: true, commandHandler.GlobalModules);
        if (guildId != 0)
        {
            await interactions.AddModulesToGuildAsync(guildId, deleteMissing: true, commandHandler.GuildModules);
            logger.LogInformation("Registered {Global} global and {Guild} guild command modules to guild {GuildId}.",
                commandHandler.GlobalModules.Length, commandHandler.GuildModules.Length, guildId);
        }
        else
        {
            logger.LogInformation("Registered {Global} global command modules. GuildId is 0, so automatic scanning is off.",
                commandHandler.GlobalModules.Length);
        }
        await RemovePreviousGuildCommandsAsync(guildId);
        _commandsRegistered = true;
    }

    /// <summary>Guild commands stay registered when GuildId changes, so remove them from the previous server.</summary>
    private async Task RemovePreviousGuildCommandsAsync(ulong guildId)
    {
        var previous = await state.ReadAsync(s => s.CommandsGuildId);
        if (previous == guildId)
        {
            return;
        }

        if (previous != 0)
        {
            try
            {
                // If the bot has left that server, nobody there can see its commands anyway.
                if (await client.Rest.GetGuildAsync(previous) is { } guild)
                {
                    await guild.BulkOverwriteApplicationCommandsAsync([]);
                    logger.LogInformation("Removed the commands from the previous guild {GuildId}.", previous);
                }
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogWarning("Couldn't remove the commands from the previous guild {GuildId}: {Error}", previous, ex.Message);
            }
        }
        await state.UpdateAsync(s => s.CommandsGuildId = guildId);
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
