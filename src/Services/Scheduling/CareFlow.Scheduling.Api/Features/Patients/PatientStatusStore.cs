using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Scheduling.Api.Infrastructure.Persistence;
using CareFlow.Scheduling.Domain.Patients;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Scheduling.Api.Features.Patients;

/// <summary>Acesso à projeção de pacientes pelos consumidores de eventos.</summary>
internal static class PatientStatusStore
{
    /// <summary>
    /// Garante que a linha existe e a trava para atualização. Eventos de um mesmo paciente (cadastro,
    /// concessão, revogação) chegam em qualquer ordem e podem ser processados ao mesmo tempo: o
    /// <c>ON CONFLICT DO NOTHING</c> evita a corrida na criação e o <c>FOR UPDATE</c> serializa a aplicação.
    /// A trava também serializa com a reserva, que lê a mesma linha com <c>FOR SHARE</c>.
    /// </summary>
    public static async Task<PatientStatus> LockAsync(
        SchedulingDbContext db,
        Guid tenantId,
        Guid patientRef,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (patientRef == Guid.Empty)
        {
            throw new PoisonMessageException("Agenda.PacienteInvalido");
        }

        var initial = PatientStatus.Track(tenantId, patientRef, now);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO patient_statuses (tenant_id, patient_ref, is_registered, scheduling_consent, consent_version, updated_at)
            VALUES ({initial.TenantId}, {initial.PatientRef}, false, 'Unknown', 0, {initial.UpdatedAt})
            ON CONFLICT (tenant_id, patient_ref) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);

        return await db.PatientStatuses
            .FromSql($"SELECT * FROM patient_statuses WHERE tenant_id = {tenantId} AND patient_ref = {patientRef} FOR UPDATE")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static void EnsureValidVersion(long version)
    {
        if (version <= 0)
        {
            throw new PoisonMessageException("Agenda.VersaoDeConsentimentoInvalida");
        }
    }
}
