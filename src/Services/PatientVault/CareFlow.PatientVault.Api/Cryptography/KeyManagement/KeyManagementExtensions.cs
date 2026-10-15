using Microsoft.Extensions.Options;

namespace CareFlow.PatientVault.Api.Cryptography.KeyManagement;

internal static class KeyManagementExtensions
{
    public const string ProviderKey = "KeyManagement:Provider";
    public const string LocalProvider = "Local";

    /// <summary>
    /// Escolhe o provedor de chaves pela configuração. O provedor local é recusado em produção:
    /// lá a KEK precisa morar num KMS/HSM gerenciado (decisão adiada até a escolha da nuvem).
    /// </summary>
    public static IServiceCollection AddKeyManagement(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var provider = configuration[ProviderKey];
        if (!string.Equals(provider, LocalProvider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configure {ProviderKey}. Provedores disponíveis: {LocalProvider}. Azure Key Vault ou AWS KMS entram quando a nuvem for escolhida.");
        }

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(
                "O provedor de chaves local não pode ser usado em produção. Configure um KMS gerenciado.");
        }

        services.AddOptions<LocalKeyManagementOptions>().Bind(configuration.GetSection(LocalKeyManagementOptions.SectionName));
        services.AddSingleton<IKeyManagementService, LocalKeyManagementService>();
        return services;
    }

    /// <summary>Instancia o KMS na subida, para que uma chave ausente ou inválida derrube o processo cedo.</summary>
    public static void ValidateKeyManagementOnStart(this IServiceProvider services) =>
        _ = services.GetRequiredService<IKeyManagementService>();
}
