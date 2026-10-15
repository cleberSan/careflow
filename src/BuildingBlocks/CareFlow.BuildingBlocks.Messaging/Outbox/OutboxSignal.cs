namespace CareFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// Acorda o despachante logo depois de um <c>SaveChanges</c> com eventos novos, para não esperar o próximo
/// ciclo de polling. É só uma otimização de latência: sem o sinal, o polling publica do mesmo jeito.
/// </summary>
public sealed class OutboxSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        // Um sinal pendente basta; sinais extras seriam ciclos vazios.
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Outro Notify chegou primeiro; o despachante já vai acordar.
            }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}
