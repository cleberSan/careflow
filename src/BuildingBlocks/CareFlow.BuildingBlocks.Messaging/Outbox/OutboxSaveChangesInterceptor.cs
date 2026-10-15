using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CareFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>Sinaliza o despachante depois que eventos novos foram gravados (ou confirmados, se havia transação).</summary>
internal sealed class OutboxSaveChangesInterceptor(OutboxSignal signal) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private bool _pending;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Track(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Track(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        NotifyIfCommitted(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        NotifyIfCommitted(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData) => NotifyPending();

    public Task TransactionCommittedAsync(
        System.Data.Common.DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        NotifyPending();
        return Task.CompletedTask;
    }

    private void Track(DbContext? context)
    {
        if (context is not null && context.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added))
        {
            _pending = true;
        }
    }

    // Dentro de uma transação explícita o evento só fica visível no commit; avisar antes acordaria o despachante à toa.
    private void NotifyIfCommitted(DbContext? context)
    {
        if (context?.Database.CurrentTransaction is null)
        {
            NotifyPending();
        }
    }

    private void NotifyPending()
    {
        if (_pending)
        {
            _pending = false;
            signal.Notify();
        }
    }
}
