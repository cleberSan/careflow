using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Persistence.Demo;

using Microsoft.AspNetCore.Authorization;

namespace CareFlow.Triage.Api.Common.Security;

internal static class AuthorizationPolicies
{
    public const string SubmitScope = "triage.submit";
    public const string ReadScope = "triage.read";
    public const string ReviewScope = "triage.review";

    public const string Submit = "Triage.Submit";
    public const string Read = "Triage.Read";
    public const string Review = "Triage.Review";

    // Só existe no ambiente de demonstração (Demo:PurgeEnabled): o serviço Demo apaga a clínica sintética da sessão.
    public const string DemoPurge = "Demo.Purge";

    // Quem envia a queixa (recepção, app do paciente) não lê as triagens dos outros nem revisa; quem revisa é
    // profissional de saúde e também lê. Menor privilégio.
    public static AuthorizationBuilder AddTriagePolicies(this AuthorizationBuilder builder) =>
        builder
            .AddTenantScopePolicy(DemoPurge, DemoPurgeEndpoint.Scope)
            .AddTenantScopePolicy(Submit, SubmitScope)
            .AddTenantScopePolicy(Read, ReadScope, ReviewScope)
            .AddTenantScopePolicy(Review, ReviewScope);
}
