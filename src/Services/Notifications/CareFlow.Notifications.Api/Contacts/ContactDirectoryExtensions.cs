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
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "O emissor interno local de tokens de serviço não pode ser usado em produção. Use client credentials do IdP.");
        }

        services.AddOptions<ServiceTokenOptions>()
            .BindConfiguration(ServiceTokenOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<VaultOptions>()
            .BindConfiguration(VaultOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ServiceTokenIssuer>();
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
