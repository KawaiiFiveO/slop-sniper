using Microsoft.Extensions.DependencyInjection;
using SlopSniper.Services;

namespace SlopSniper.Provenance;

public static class ProvenanceRegistration
{
    /// <summary>Adds OpenAI provenance checks: automatic scanning in GuildId and the global manual check.</summary>
    public static IServiceCollection AddProvenance(this IServiceCollection services)
    {
        services.AddHttpClient(ProvenanceClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<ProvenanceClient>();
        services.AddSingleton<ResultCache>();
        services.AddSingleton<OpenAiStatus>();
        services.AddSingleton<ProvenanceStats>();
        services.AddSingleton<ProvenanceReporter>();
        services.AddSingleton<ImageChecker>();
        services.AddSingleton<ProvenanceQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<ProvenanceQueue>());
        services.AddSingleton<IMessageModule, ProvenanceScanner>();
        services.AddSingleton<IStatusSection, ProvenanceStatusSection>();
        services.AddSingleton(new CommandModule(typeof(CheckImageCommand), Global: true));
        return services;
    }
}
