using Microsoft.EntityFrameworkCore;

namespace CareFlow.BuildingBlocks.Persistence;

/// <summary>
/// Trava transacional por paciente (<c>pg_advisory_xact_lock</c>), para serializar uma escrita com a eliminação do
/// mesmo pseudônimo quando não há linha para travar (ex.: o primeiro consentimento de uma finalidade). Solta sozinha
/// no fim da transação; exige uma transação aberta.
/// </summary>
public static class SubjectLock
{
    public static async Task AcquireAsync(DbContext db, Guid patientRef, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A trava por paciente só vale dentro de uma transação.");
        }

        // Colisões entre pseudônimos diferentes só serializam mais do que o necessário; nunca liberam algo indevido.
        var key = BitConverter.ToInt64(patientRef.ToByteArray(), 0);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken).ConfigureAwait(false);
    }
}
