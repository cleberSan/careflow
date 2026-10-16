using Microsoft.Extensions.Hosting;

namespace CareFlow.BuildingBlocks.AspNetCore.Hosting;

/// <summary>
/// Ambientes em que o CareFlow roda (ADR-0013). Além de <c>Development</c>, <c>Testing</c> e <c>Production</c>, existe
/// <c>Demo</c>: a demonstração pública hospedada, com dados só sintéticos. Ela é tratada como produção em tudo o que
/// protege segredo e dado (KMS gerenciado, modelo real desligado), mas mantém o que só a demonstração precisa
/// (limpeza das clínicas de visitantes, caixa de entrada simulada, emissor de tokens do Demo).
/// </summary>
public static class CareFlowEnvironments
{
    public const string HostedDemo = "Demo";

    /// <summary>A demonstração pública hospedada.</summary>
    public static bool IsHostedDemo(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsEnvironment(HostedDemo);
    }

    /// <summary>Produção ou demonstração hospedada: qualquer ambiente exposto à internet.</summary>
    public static bool IsInternetFacing(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsProduction() || environment.IsHostedDemo();
    }
}
