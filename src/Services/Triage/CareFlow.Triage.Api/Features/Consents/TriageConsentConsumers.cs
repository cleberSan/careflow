using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Contracts.Consents;
using CareFlow.Triage.Api.Infrastructure.Persistence;
using CareFlow.Triage.Domain.Consents;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Triage.Api.Features.Consents;

/// <summary>Concessão de consentimento. Só a finalidade <c>ai-triage</c> interessa; versões já conhecidas são ignoradas.</summary>
internal sealed class ConsentGrantedConsumer(TriageDbContext db, TimeProvider time) : IIntegrationEventConsumer<ConsentGranted>
{
    public async Task HandleAsync(ConsentGranted @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(@event.Purpose, ConsentPurposes.AiTriage, StringComparison.Ordinal))
        {
            return;
        }

        var now = time.GetUtcNow();
        var consent = await TriageConsentStore.LockAsync(db, context.TenantId, @event.PatientRef, @event.Version, now, cancellationToken).ConfigureAwait(false);
        consent.ApplyGranted(@event.Version, now);
    }
}

/// <summary>
/// Revogação do consentimento de triagem por IA. Vale para as próximas triagens: a partir daqui, a queixa não sai
/// do serviço e só as regras de alarme sugerem. Triagens já feitas continuam registradas, como a trilha exige.
/// </summary>
internal sealed class ConsentRevokedConsumer(TriageDbContext db, TimeProvider time) : IIntegrationEventConsumer<ConsentRevoked>
{
    public async Task HandleAsync(ConsentRevoked @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(@event.Purpose, ConsentPurposes.AiTriage, StringComparison.Ordinal))
        {
            return;
        }

        var now = time.GetUtcNow();
        var consent = await TriageConsentStore.LockAsync(db, context.TenantId, @event.PatientRef, @event.Version, now, cancellationToken).ConfigureAwait(false);
        consent.ApplyRevoked(@event.Version, now);
    }
}

internal static class TriageConsentStore
{
    /// <summary>
    /// Garante a linha do consentimento e a trava para atualização. Concessão e revogação podem ser processadas
    /// ao mesmo tempo: o <c>ON CONFLICT DO NOTHING</c> evita a corrida na criação e o <c>FOR UPDATE</c> serializa
    /// a aplicação; a versão decide qual vale.
    /// </summary>
    public static async Task<TriageConsent> LockAsync(
        TriageDbContext db,
        Guid tenantId,
        Guid patientRef,
        long version,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (patientRef == Guid.Empty)
        {
            throw new PoisonMessageException("Triagem.PacienteInvalido");
        }

        if (version <= 0)
        {
            throw new PoisonMessageException("Triagem.VersaoDeConsentimentoInvalida");
        }

        var initial = TriageConsent.Track(tenantId, patientRef, now);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO triage_consents (tenant_id, patient_ref, state, version, updated_at)
            VALUES ({initial.TenantId}, {initial.PatientRef}, 'Unknown', 0, {initial.UpdatedAt})
            ON CONFLICT (tenant_id, patient_ref) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);

        return await db.Consents
            .FromSql($"SELECT * FROM triage_consents WHERE tenant_id = {tenantId} AND patient_ref = {patientRef} FOR UPDATE")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Consentimento ainda não visto conta como não concedido: sem evento, a queixa não sai.</summary>
    public static async Task<bool> AllowsAiAsync(TriageDbContext db, Guid patientRef, CancellationToken cancellationToken) =>
        await db.Consents.AsNoTracking()
            .AnyAsync(c => c.PatientRef == patientRef && c.State == TriageConsentState.Granted, cancellationToken)
            .ConfigureAwait(false);
}
