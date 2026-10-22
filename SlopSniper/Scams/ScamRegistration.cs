using Microsoft.Extensions.DependencyInjection;
using SlopSniper.Services;

namespace SlopSniper.Scams;

public static class ScamRegistration
{
    /// <summary>Adds scam image detection in GuildId, which deletes scam posts and times out their author.</summary>
    public static IServiceCollection AddScams(this IServiceCollection services)
    {
        services.AddSingleton<BurstTracker>();
        services.AddSingleton<KnownScams>();
        services.AddSingleton<ScamImageHasher>();
        services.AddSingleton<OcrRunner>();
        services.AddSingleton<ScamStats>();
        services.AddSingleton<ScamModerator>();
        services.AddSingleton<ScamResponder>();
        services.AddSingleton<ScamQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<ScamQueue>());
        services.AddSingleton<IMessageModule, ScamDetector>();
        services.AddSingleton<IStatusSection, ScamStatusSection>();
        services.AddSingleton(new CommandModule(typeof(ScamButtons), Global: false));
        services.AddSingleton(new CommandModule(typeof(ScamReportCommand), Global: false));
        services.AddSingleton(new CommandModule(typeof(ScamDebugCommand), Global: true));
        return services;
    }
}
