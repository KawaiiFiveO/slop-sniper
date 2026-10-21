using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper;
using SlopSniper.Modules;
using SlopSniper.Options;
using SlopSniper.Provenance;
using SlopSniper.Services;
using SlopSniper.State;

var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

// Used by update.sh to compare the installed version with the latest release.
if (args is ["--version"])
{
    Console.WriteLine(version);
    return 0;
}

var configPath = Path.Combine(AppContext.BaseDirectory, BotOptions.FileName);
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"{BotOptions.FileName} not found at {configPath}. Copy config.example.json to {BotOptions.FileName} and fill it in.");
    return ExitCodes.ConfigError;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Configuration
    .AddJsonFile(BotOptions.FileName, optional: false, reloadOnChange: false)
    .AddEnvironmentVariables(ConfigReloader.EnvironmentPrefix);

// Uses journald log formatting and sd_notify readiness when running under systemd; no-op otherwise.
builder.Services.AddSystemd();

// Each request logs its URL, which for Discord attachments identifies the image; keep those out of the logs.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddOptions<BotOptions>().Bind(builder.Configuration);
builder.Services.AddSingleton<IValidateOptions<BotOptions>, BotOptionsValidator>();

builder.Services.AddHttpClient(ImageDownloader.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"slop-sniper/{version.Split('+')[0]}");
});

// Automatic scanning needs message events. As a user app only (GuildId 0), the bot just answers commands, so it
// doesn't ask for them. Parsed by hand because a bad value is reported by the validator below, not here.
var scansAutomatically = ulong.TryParse(builder.Configuration["GuildId"], out var guildId) && guildId != 0;
builder.Services.AddSingleton(new DiscordSocketConfig
{
    // Message Content is a privileged intent; it has to be enabled in the Developer Portal too.
    GatewayIntents = scansAutomatically
        ? GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent
        : GatewayIntents.Guilds,
});
builder.Services.AddSingleton<DiscordSocketClient>();
builder.Services.AddSingleton(sp => new InteractionService(sp.GetRequiredService<DiscordSocketClient>()));
builder.Services.AddSingleton<CommandHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<ConfigReloader>();
builder.Services.AddSingleton<Reporter>();
builder.Services.AddSingleton<ImageDownloader>();
builder.Services.AddSingleton(new CommandModule(typeof(SniperCommands), Global: false));
builder.Services.AddSingleton(new CommandModule(typeof(ReloadCommand), Global: true));

// Hosted services stop in reverse order: the scanner stops taking messages, then the modules' queues stop, then
// the Discord client disconnects. So modules are added between the two.
builder.Services.AddHostedService<BotHostedService>();
if (IsEnabled("Provenance"))
{
    builder.Services.AddProvenance();
}
builder.Services.AddHostedService<MessageScanner>();

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

// Settings from the flat config.json used before modules had their own sections would be silently ignored.
if (LegacyConfig.FindMovedSettings(builder.Configuration) is { Count: > 0 } moved)
{
    foreach (var setting in moved)
    {
        logger.LogCritical("Invalid {ConfigFile}: {Failure}", BotOptions.FileName, setting);
    }
    return ExitCodes.ConfigError;
}

try
{
    _ = host.Services.GetRequiredService<IOptions<BotOptions>>().Value;
}
catch (OptionsValidationException ex)
{
    foreach (var failure in ex.Failures)
    {
        logger.LogCritical("Invalid {ConfigFile}: {Failure}", BotOptions.FileName, failure);
    }
    return ExitCodes.ConfigError;
}
catch (InvalidOperationException ex)
{
    // Thrown by the configuration binder when a value can't be converted, e.g. a non-numeric GuildId.
    logger.LogCritical("Invalid {ConfigFile}: {Error}", BotOptions.FileName, ex.Message);
    return ExitCodes.ConfigError;
}

logger.LogInformation("slop-sniper {Version}", version);

await host.RunAsync();
return Environment.ExitCode;

// Read before the options are bound, which only happens once the host is built. A value that isn't a bool counts
// as disabled here and fails when the options are bound.
bool IsEnabled(string module) => bool.TryParse(builder.Configuration[$"{module}:Enabled"], out var enabled) && enabled;
