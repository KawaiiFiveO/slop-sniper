using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;

namespace SlopSniper.Services;

/// <summary>Routes incoming interactions to the command modules and reports failed commands to the user.</summary>
public sealed class CommandHandler(
    DiscordSocketClient client,
    InteractionService interactions,
    IEnumerable<CommandModule> modules,
    IServiceProvider services,
    IOptions<BotOptions> options,
    ILogger<CommandHandler> logger)
{
    private HashSet<string> _globalCommandNames = [];

    /// <summary>Registered globally, so they also work as a user app anywhere.</summary>
    public ModuleInfo[] GlobalModules { get; private set; } = [];

    /// <summary>Registered to GuildId only.</summary>
    public ModuleInfo[] GuildModules { get; private set; } = [];

    public async Task InitializeAsync()
    {
        List<ModuleInfo> global = [];
        List<ModuleInfo> guild = [];
        foreach (var module in modules)
        {
            var info = await interactions.AddModuleAsync(module.Type, services);
            (module.Global ? global : guild).Add(info);
        }
        GlobalModules = [.. global];
        GuildModules = [.. guild];
        _globalCommandNames = global.SelectMany(GetCommandNames).ToHashSet();

        client.InteractionCreated += HandleInteractionAsync;
        interactions.InteractionExecuted += OnInteractionExecutedAsync;
    }

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        // Outside the home server only global commands are served; their preconditions decide who can use them.
        // Guild commands left behind in another server, and components there, are ignored.
        if (interaction.GuildId != options.Value.GuildId
            && !(interaction is SocketCommandBase command && _globalCommandNames.Contains(command.CommandName)))
        {
            return;
        }

        var context = new SocketInteractionContext(client, interaction);
        await interactions.ExecuteCommandAsync(context, services);
    }

    /// <summary>The names Discord sends in an interaction for a module's top-level commands.</summary>
    private static IEnumerable<string> GetCommandNames(ModuleInfo module) =>
        module.IsSlashGroup
            ? [module.SlashGroupName]
            : module.SlashCommands.Select(c => c.Name).Concat(module.ContextCommands.Select(c => c.Name));

    private async Task OnInteractionExecutedAsync(ICommandInfo command, IInteractionContext context, IResult result)
    {
        // UnknownCommand: not one of ours, so stay silent.
        if (result.IsSuccess || result.Error == InteractionCommandError.UnknownCommand)
        {
            return;
        }

        // Exceptions are already logged by InteractionService through its Log event.
        if (result.Error != InteractionCommandError.Exception)
        {
            logger.LogWarning("Command {Command} failed for {User}: {Error} {Reason}",
                command?.Name, context.User.Username, result.Error, result.ErrorReason);
        }

        var message = result.Error switch
        {
            InteractionCommandError.UnmetPrecondition => result.ErrorReason,
            InteractionCommandError.BadArgs or InteractionCommandError.ConvertFailed or InteractionCommandError.ParseFailed
                => "Invalid command arguments.",
            _ => "Something went wrong running that command. Check the bot logs for details.",
        };

        try
        {
            if (context.Interaction.HasResponded)
            {
                await context.Interaction.FollowupAsync(message, ephemeral: true);
            }
            else
            {
                await context.Interaction.RespondAsync(message, ephemeral: true);
            }
        }
        catch (Exception ex) when (ex is Discord.Net.HttpException or TimeoutException)
        {
            // The interaction may have expired (3s to respond, 15 min for follow-ups).
            logger.LogWarning(ex, "Could not send the error message for {Command}.", command?.Name);
        }
    }
}
