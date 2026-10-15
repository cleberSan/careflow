using CareFlow.Notifications.Api.Channels.Resilience;
using CareFlow.Notifications.Api.Channels.Simulated;
using CareFlow.Notifications.Domain.Channels;

using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Channels;

internal static class ChannelServiceCollectionExtensions
{
    /// <summary>
    /// Os três canais, cada um com HttpClient nomeado, provedor simulado e pipeline de resiliência próprio
    /// (o circuit breaker de um canal não afeta os outros).
    /// </summary>
    public static IServiceCollection AddNotificationChannels(this IServiceCollection services)
    {
        services.AddOptions<ChannelResilienceOptions>()
            .BindConfiguration(ChannelResilienceOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(o => o.AttemptTimeout <= o.TotalTimeout, "Notifications:Resilience:AttemptTimeout não pode passar do TotalTimeout.")
            .ValidateOnStart();
        services.AddOptions<FaultInjectionOptions>()
            .BindConfiguration(FaultInjectionOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate<IHostEnvironment>(
                (options, environment) => !(options.ClinicOverridesEnabled && environment.IsProduction()),
                "FaultInjection:ClinicOverridesEnabled não pode ser ligado em produção.")
            .ValidateOnStart();

        services.AddSingleton<SimulatedProviderLedger>();
        services.AddSingleton<ChannelCircuits>();
        services.AddSingleton<ClinicIsolation>();
        services.AddScoped<ClinicFaultResolver>();
        services.AddSingleton<ChannelGateway>();
        services.AddSingleton<ChannelRouter>();

        foreach (var channel in Enum.GetValues<Channel>())
        {
            var baseAddress = new Uri($"https://{channel.Slug()}.simulated-provider.invalid/");

            services.AddHttpClient(ChannelGateway.SendClientName(channel), client => client.BaseAddress = baseAddress)
                .ConfigurePrimaryHttpMessageHandler(sp => CreateSimulatedProvider(sp, channel))
                .AddResilienceHandler($"{ChannelGateway.SendClientName(channel)}-pipeline", (builder, context) =>
                {
                    var services = context.ServiceProvider;
                    var faults = services.GetRequiredService<IOptionsMonitor<FaultInjectionOptions>>();
                    ChannelPipelines.ConfigureSend(
                        builder,
                        channel,
                        services.GetRequiredService<IOptions<ChannelResilienceOptions>>().Value,
                        services.GetRequiredService<ChannelCircuits>(),
                        context.InstanceName,
                        () => faults.CurrentValue);
                })

                // Um pipeline (e um circuit breaker) por partição; a lista de partições é fixa e curta.
                .SelectPipelineBy(_ => request => ChannelCall.Of(request)?.Partition ?? ClinicIsolation.Shared);

            services.AddHttpClient(ChannelGateway.StatusClientName(channel), client => client.BaseAddress = baseAddress)
                .ConfigurePrimaryHttpMessageHandler(sp => CreateSimulatedProvider(sp, channel))
                .AddResilienceHandler($"{ChannelGateway.StatusClientName(channel)}-pipeline", (builder, context) =>
                {
                    var services = context.ServiceProvider;
                    var faults = services.GetRequiredService<IOptionsMonitor<FaultInjectionOptions>>();
                    ChannelPipelines.ConfigureStatus(
                        builder,
                        channel,
                        services.GetRequiredService<IOptions<ChannelResilienceOptions>>().Value,
                        () => faults.CurrentValue);
                });
        }

        services.AddHealthChecks().AddCheck<ChannelsHealthCheck>("notification-channels", tags: [ChannelsHealthCheck.Tag]);
        return services;
    }

    private static SimulatedProviderHandler CreateSimulatedProvider(IServiceProvider services, Channel channel) =>
        new(
            channel,
            services.GetRequiredService<SimulatedProviderLedger>(),
            services.GetRequiredService<IOptionsMonitor<FaultInjectionOptions>>(),
            services.GetRequiredService<TimeProvider>());
}
