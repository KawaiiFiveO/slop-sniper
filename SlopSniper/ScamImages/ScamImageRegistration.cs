using Microsoft.Extensions.DependencyInjection;
using SlopSniper.Services;

namespace SlopSniper.ScamImages;

public static class ScamImageRegistration
{
    /// <summary>Adds scam image detection in GuildId, which deletes scam posts and times out their author.</summary>
    public static IServiceCollection AddScamImages(this IServiceCollection services)
    {
        services.AddSingleton<BurstTracker>();
        services.AddSingleton<ScamStats>();
        services.AddSingleton<ScamModerator>();
        services.AddSingleton<ScamQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<ScamQueue>());
        services.AddSingleton<IMessageModule, ScamDetector>();
        services.AddSingleton<IStatusSection, ScamStatusSection>();
        services.AddSingleton(new CommandModule(typeof(ScamButtons), Global: false));
        return services;
    }
}
