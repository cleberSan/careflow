using CareFlow.BuildingBlocks.AspNetCore.Http;
using CareFlow.BuildingBlocks.AspNetCore.Security;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.PatientVault.Api.Common;
using CareFlow.PatientVault.Api.Common.Observability;
using CareFlow.PatientVault.Api.Common.Security;
using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;

using Microsoft.AspNetCore.Http.HttpResults;

namespace CareFlow.PatientVault.Api.Features.Keys;

public sealed record KeyRotationResponse(int Version);

/// <summary>
/// Rotação da chave da clínica (ADR-0012): cria a versão nova, migra os registros em lotes e destrói a versão
/// antiga quando nada mais depende dela. Escopo próprio, <c>vault.keys</c>, que não lê nem escreve pacientes.
/// </summary>
internal static class KeyEndpoints
{
    public static IServiceCollection AddKeyFeatures(this IServiceCollection services) =>
        services.AddScoped<KeyMaintenance>();

    public static IEndpointRouteBuilder MapKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var keys = app.MapGroup("/v1/keys").WithTags("Keys").RequireAuthorization(AuthorizationPolicies.Keys);

        keys.MapPost("/rotation", RotateAsync)
            .WithName("RotateTenantKey")
            .WithSummary("Cria a próxima versão da chave da clínica, que passa a ser a atual.");

        keys.MapPost("/maintenance", MaintainAsync)
            .WithName("RunKeyMaintenance")
            .WithSummary("Migra um lote de registros para a chave atual (esquema e versão). Repita até restar zero.");

        keys.MapDelete("/{version:int}", DestroyAsync)
            .WithName("DestroyTenantKey")
            .WithSummary("Destrói uma versão antiga da chave da clínica que nenhum registro usa mais.");

        return app;
    }

    private static async Task<Created<KeyRotationResponse>> RotateAsync(
        TenantKeyRing keys, VaultDbContext db, ITenantContext tenant, VaultMetrics metrics, TimeProvider time, HttpContext http, CancellationToken cancellationToken)
    {
        // O registro de auditoria entra antes: a rotação grava os dois na mesma transação.
        db.Record(tenant.TenantId, tenant.TenantId, AuditActor.From(http.User), VaultAudit.TenantKeyRotated, VaultAudit.TenantKeyResource, time.GetUtcNow());
        var key = await keys.RotateAsync(cancellationToken).ConfigureAwait(false);
        metrics.TenantKeyRotated();
        return TypedResults.Created($"/v1/keys/{key.Version}", new KeyRotationResponse(key.Version));
    }

    private static async Task<Ok<KeyMaintenanceResult>> MaintainAsync(KeyMaintenance maintenance, int? batchSize, CancellationToken cancellationToken) =>
        TypedResults.Ok(await maintenance.RunBatchAsync(batchSize ?? 100, cancellationToken).ConfigureAwait(false));

    private static async Task<Results<NoContent, ProblemHttpResult>> DestroyAsync(
        int version, KeyMaintenance maintenance, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await maintenance.DestroyAsync(version, AuditActor.From(http.User), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? result.Error.ToProblem() : TypedResults.NoContent();
    }
}
