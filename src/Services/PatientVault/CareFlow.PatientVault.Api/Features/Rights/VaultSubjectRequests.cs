using System.Security.Cryptography;

using CareFlow.BuildingBlocks.Messaging.Consuming;
using CareFlow.BuildingBlocks.Messaging.Rights;
using CareFlow.Contracts.Rights;
using CareFlow.PatientVault.Api.Common;
using CareFlow.PatientVault.Api.Common.Observability;
using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.PatientVault.Api.Features.Rights;

/// <summary>
/// Resposta do cofre a um pedido do titular (ADR-0012). Acesso e portabilidade: decifra o cadastro e o cifra para o
/// pedido. Eliminação: destrói a chave do paciente (crypto-shredding), apaga o blind index do CPF e mantém só o
/// pseudônimo, que as outras bases e a trilha de auditoria referenciam.
/// </summary>
internal sealed class SubjectRequestOpenedConsumer(VaultDbContext db, PatientEraser eraser, PatientKeyRing keys, VaultMetrics metrics, TimeProvider time)
    : IIntegrationEventConsumer<SubjectRequestOpened>
{
    public async Task HandleAsync(SubjectRequestOpened @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        var now = time.GetUtcNow();
        var record = await db.Patients.FirstOrDefaultAsync(p => p.PatientRef == @event.PatientRef, cancellationToken).ConfigureAwait(false);

        if (@event.Kind == SubjectRequestKinds.Erasure)
        {
            if (record is null)
            {
                db.AnswerSubjectNotFound(context.TenantId, @event.RequestId, SubjectRequestServices.Vault, now);
                return;
            }

            var lines = await eraser.EraseAsync(record, VaultAudit.Erased, now, cancellationToken).ConfigureAwait(false);
            db.AnswerErasure(context.TenantId, @event.RequestId, SubjectRequestServices.Vault, lines, now);
            return;
        }

        if (!SubjectRequestKinds.IsExport(@event.Kind))
        {
            throw new InvalidOperationException("Tipo de pedido desconhecido.");
        }

        // Paciente eliminado não tem mais o que exportar do cofre: o pedido é negado como titular não encontrado.
        if (record is null || record.IsErased)
        {
            db.AnswerSubjectNotFound(context.TenantId, @event.RequestId, SubjectRequestServices.Vault, now);
            return;
        }

        var data = PatientProtector.Unprotect(record, await keys.ForReadingAsync(record, cancellationToken).ConfigureAwait(false));
        var section = new VaultSection(
            record.PatientRef, data.FullName, data.Cpf, data.BirthDate, data.Email, data.Phone, record.CreatedAt, record.UpdatedAt);

        db.AnswerExport(context.TenantId, @event, SubjectRequestServices.Vault, section, now);
        db.Record(context.TenantId, record.PatientRef, VaultAudit.RightsActor, VaultAudit.Exported, now);
        metrics.Exported();
    }
}

/// <summary>
/// Depois de uma restauração de backup, o banco pode ter voltado com a chave do paciente. Reaplica a eliminação; num
/// banco que não foi restaurado, não muda nada.
/// </summary>
internal sealed class ErasureReappliedConsumer(VaultDbContext db, PatientEraser eraser, TimeProvider time)
    : IIntegrationEventConsumer<ErasureReapplied>
{
    public async Task HandleAsync(ErasureReapplied @event, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(context);

        var record = await db.Patients.FirstOrDefaultAsync(p => p.PatientRef == @event.PatientRef, cancellationToken).ConfigureAwait(false);
        if (record is not null)
        {
            await eraser.EraseAsync(record, VaultAudit.ErasureReapplied, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Eliminação de um paciente no cofre. Idempotente: repetir não muda nada e não falha.</summary>
internal sealed partial class PatientEraser(VaultDbContext db, PatientKeyRing keys, VaultMetrics metrics, ILogger<PatientEraser> logger)
{
    public async Task<IReadOnlyList<ErasureLine>> EraseAsync(PatientRecord record, string auditAction, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A linha da chave é a única cópia: sem ela, os campos cifrados com a chave do paciente não abrem mais.
        var destroyed = await keys.DestroyAsync(record.PatientRef, cancellationToken).ConfigureAwait(false);

        // Registro do esquema antigo (cifrado com a chave da clínica, que continua viva): os campos são sobrescritos
        // por bytes aleatórios do mesmo tamanho. Não há chave nenhuma que os abra.
        var overwritten = record.KeyScheme == KeyScheme.ClinicKey;
        if (overwritten)
        {
            record.FullName = RandomNumberGenerator.GetBytes(record.FullName.Length);
            record.Cpf = RandomNumberGenerator.GetBytes(record.Cpf.Length);
            record.BirthDate = RandomNumberGenerator.GetBytes(record.BirthDate.Length);
            record.Email = record.Email is null ? null : RandomNumberGenerator.GetBytes(record.Email.Length);
            record.Phone = record.Phone is null ? null : RandomNumberGenerator.GetBytes(record.Phone.Length);
            record.KeyScheme = KeyScheme.PatientKey;
        }

        var indexErased = record.CpfIndex is not null;
        var wasErased = record.IsErased;
        record.CpfIndex = null;
        record.ErasedAt ??= now;
        record.UpdatedAt = now;

        if (!wasErased || destroyed > 0 || overwritten)
        {
            db.Record(record.TenantId, record.PatientRef, VaultAudit.RightsActor, auditAction, now);
            if (destroyed > 0)
            {
                metrics.PatientKeyDestroyed();
            }

            LogErased(logger, destroyed > 0, overwritten);
        }

        var shredded = destroyed > 0 || overwritten ? 1 : 0;
        return
        [
            new(RetentionPolicy.Items.VaultPersonalData, RetentionActions.Destroyed, shredded),
            new(RetentionPolicy.Items.VaultCpfIndex, RetentionActions.Erased, indexErased ? 1 : 0),
            new(RetentionPolicy.Items.VaultPseudonym, RetentionActions.Retained, 1),
        ];
    }

    [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "Paciente eliminado no cofre (chave destruída: {KeyDestroyed}, campos sobrescritos: {Overwritten})")]
    private static partial void LogErased(ILogger logger, bool keyDestroyed, bool overwritten);
}
