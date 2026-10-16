using CareFlow.BuildingBlocks.AspNetCore.Security.ServiceTokens;

using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace CareFlow.Notifications.Api.Contacts;

internal static class ContactDirectoryExtensions
{
    /// <summary>
    /// Cliente do cofre com o pipeline padrão do Microsoft.Extensions.Http.Resilience (timeout, retry com jitter e
    /// circuit breaker). O emissor local de tokens é recusado em produção.
    /// </summary>
    public static IServiceCollection AddContactDirectory(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddLocalServiceTokens(environment, VaultContactDirectory.Grant);
        services.AddOptions<VaultOptions>()
            .BindConfiguration(VaultOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<IContactDirectory, VaultContactDirectory>(VaultContactDirectory.ClientName, (provider, client) =>
            {
                client.BaseAddress = provider.GetRequiredService<IOptions<VaultOptions>>().Value.BaseAddress;
            })
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.Retry.MaxRetryAttempts = 2;
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            });

        return services;
    }
}
