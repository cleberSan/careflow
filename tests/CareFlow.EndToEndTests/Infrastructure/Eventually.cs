namespace CareFlow.EndToEndTests.Infrastructure;

/// <summary>
/// Espera uma condição de consistência eventual. Falha com a última observação se o prazo acabar, para o
/// erro dizer o que estava errado e não só que demorou.
/// </summary>
public static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<T> GetAsync<T>(Func<Task<T>> probe, Func<T, bool> done, TimeSpan? timeout = null, string? description = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? DefaultTimeout);
        var delay = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            var value = await probe();
            if (done(value))
            {
                return value;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail($"Condição não atingida a tempo: {description ?? "sem descrição"}. Último valor: {value}");
            }

            await Task.Delay(delay, TestContext.Current.CancellationToken);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
        }
    }

    public static Task SucceedsAsync(Func<Task<bool>> probe, TimeSpan? timeout = null, string? description = null) =>
        GetAsync(probe, ok => ok, timeout, description);
}
