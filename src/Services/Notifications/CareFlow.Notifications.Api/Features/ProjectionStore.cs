using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.Notifications.Api.Infrastructure.Persistence;
using CareFlow.Notifications.Domain.Appointments;
using CareFlow.Notifications.Domain.Clinics;
using CareFlow.Notifications.Domain.Consents;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Notifications.Api.Features;

/// <summary>Acesso às projeções pelos consumidores de eventos.</summary>
internal static class ProjectionStore
{
    /// <summary>
    /// Garante a linha da consulta e a trava para atualização. Reserva e cancelamento da mesma consulta podem
    /// ser processados ao mesmo tempo: o <c>ON CONFLICT DO NOTHING</c> evita a corrida na criação e o
    /// <c>FOR UPDATE</c> serializa a aplicação.
    /// </summary>
    public static async Task<AppointmentTrack> LockAppointmentAsync(
        NotificationsDbContext db,
        Guid tenantId,
        Guid appointmentId,
        Guid patientRef,
        DateTimeOffset startsAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (appointmentId == Guid.Empty || patientRef == Guid.Empty)
        {
            throw new PoisonMessageException("Notificacoes.ConsultaInvalida");
        }

        var initial = AppointmentTrack.Track(tenantId, appointmentId, patientRef, startsAt, now);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO appointment_tracks (tenant_id, appointment_id, patient_ref, starts_at, state, updated_at)
            VALUES ({initial.TenantId}, {initial.AppointmentId}, {initial.PatientRef}, {initial.StartsAt}, 'Unknown', {initial.UpdatedAt})
            ON CONFLICT (tenant_id, appointment_id) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);

        return await db.Appointments
            .FromSql($"SELECT * FROM appointment_tracks WHERE tenant_id = {tenantId} AND appointment_id = {appointmentId} FOR UPDATE")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Mesma estratégia para o consentimento de notificações do paciente.</summary>
    public static async Task<NotificationConsent> LockConsentAsync(
        NotificationsDbContext db,
        Guid tenantId,
        Guid patientRef,
        long version,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (patientRef == Guid.Empty)
        {
            throw new PoisonMessageException("Notificacoes.PacienteInvalido");
        }

        if (version <= 0)
        {
            throw new PoisonMessageException("Notificacoes.VersaoDeConsentimentoInvalida");
        }

        var initial = NotificationConsent.Track(tenantId, patientRef, now);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO notification_consents (tenant_id, patient_ref, state, version, updated_at)
            VALUES ({initial.TenantId}, {initial.PatientRef}, 'Unknown', 0, {initial.UpdatedAt})
            ON CONFLICT (tenant_id, patient_ref) DO NOTHING
            """,
            cancellationToken).ConfigureAwait(false);

        return await db.Consents
            .FromSql($"SELECT * FROM notification_consents WHERE tenant_id = {tenantId} AND patient_ref = {patientRef} FOR UPDATE")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Configuração da clínica, ou os padrões se ela ainda não gravou a sua.</summary>
    public static async Task<ClinicSettings> ClinicSettingsAsync(
        NotificationsDbContext db,
        Guid tenantId,
        string defaultDisplayName,
        CancellationToken cancellationToken) =>
        await db.ClinicSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? ClinicSettings.Defaults(tenantId, defaultDisplayName);
}
