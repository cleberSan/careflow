using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Notifications.Api.Channels.Simulated;
using CareFlow.Notifications.Domain.Channels;

using Microsoft.AspNetCore.Http.HttpResults;

namespace CareFlow.Notifications.Api.Features.Rights;

public sealed record SimulatedInboxMessage(string Channel, string Text, DateTimeOffset AcceptedAt);

/// <summary>
/// Caixa de entrada simulada do Painel (ADR-0012): mostra a mensagem com o código de confirmação que o provedor
/// simulado "entregou", para a demonstração seguir sem um telefone de verdade. Só existe com
/// <c>Demo:SimulatedInboxEnabled</c>, que derruba a subida em produção, e só devolve mensagens da clínica do token.
/// </summary>
internal static class SimulatedInboxEndpoint
{
    public const string EnabledSetting = "Demo:SimulatedInboxEnabled";

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>(EnabledSetting))
        {
            return false;
        }

        return environment.IsProduction()
            ? throw new InvalidOperationException($"{EnabledSetting} não pode ser ligado em produção.")
            : true;
    }

    public static WebApplication MapSimulatedInboxIfEnabled(this WebApplication app, string policyName)
    {
        if (!IsEnabled(app.Configuration, app.Environment))
        {
            return app;
        }

        app.MapGet("/v1/demo/verification-messages/{requestId:guid}", Handle)
            .RequireAuthorization(policyName)
            .WithTags("Demo")
            .WithName("GetSimulatedVerificationMessages")
            .WithSummary("Mensagens de confirmação de um pedido do titular no provedor simulado (só na demonstração).");
        return app;
    }

    private static Ok<SimulatedInboxMessage[]> Handle(Guid requestId, ITenantContext tenant, SimulatedProviderLedger ledger)
    {
        var prefix = IdentityVerificationRequestedConsumer.KeyPrefix(tenant.TenantId, requestId);
        return TypedResults.Ok(ledger.Messages
            .Where(m => m.IdempotencyKey.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(m => m.AcceptedAt)
            .Select(m => new SimulatedInboxMessage(m.Channel.Slug(), m.Text, m.AcceptedAt))
            .ToArray());
    }
}
