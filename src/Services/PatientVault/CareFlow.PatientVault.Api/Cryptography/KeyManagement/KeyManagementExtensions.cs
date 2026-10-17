using CareFlow.BuildingBlocks.AspNetCore.Hosting;

namespace CareFlow.PatientVault.Api.Cryptography.KeyManagement;

internal static class KeyManagementExtensions
{
    public const string ProviderKey = "KeyManagement:Provider";
    public const string LocalProvider = "Local";
    public const string OciVaultProvider = "OciVault";

    /// <summary>
    /// Escolhe o provedor de chaves pela configuração. O provedor local é recusado em qualquer ambiente exposto à
    /// internet (produção e demonstração hospedada): lá a KEK mora no OCI Vault (ADR-0013).
    /// </summary>
    public static IServiceCollection AddKeyManagement(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        switch (configuration[ProviderKey])
        {
            case LocalProvider when environment.IsInternetFacing():
                throw new InvalidOperationException(
                    "O provedor de chaves local não pode ser usado em produção nem na demonstração hospedada. Use KeyManagement:Provider=OciVault.");

            case LocalProvider:
                services.AddOptions<LocalKeyManagementOptions>().Bind(configuration.GetSection(LocalKeyManagementOptions.SectionName));
                services.AddSingleton<IKeyManagementService, LocalKeyManagementService>();
                return services;

            case OciVaultProvider:
                services.AddOptions<OciVaultKeyManagementOptions>()
                    .Bind(configuration.GetSection(OciVaultKeyManagementOptions.SectionName))
                    .ValidateDataAnnotations()
                    .Validate(o => o.CryptoEndpoint is { Scheme: "https" }, "KeyManagement:OciVault:CryptoEndpoint precisa ser HTTPS.")
                    .ValidateOnStart();
                services.AddSingleton<IOciCrypto, OciSdkCrypto>();
                services.AddSingleton<IKeyManagementService, OciVaultKeyManagementService>();
                return services;

            default:
                throw new InvalidOperationException(
                    $"Configure {ProviderKey}. Provedores disponíveis: {LocalProvider} (desenvolvimento) e {OciVaultProvider}.");
        }
    }

    /// <summary>Instancia o KMS na subida, para que uma chave ausente ou inválida derrube o processo cedo.</summary>
    public static void ValidateKeyManagementOnStart(this IServiceProvider services) =>
        _ = services.GetRequiredService<IKeyManagementService>();
}
