namespace CareFlow.BuildingBlocks.AspNetCore.Hosting;

/// <summary>
/// Healthcheck do container sem shell nem curl na imagem (ADR-0013): o próprio binário do serviço, chamado com
/// <c>--health-probe &lt;url&gt;</c>, faz um GET no loopback e sai com 0 (saudável) ou 1, sem montar o host.
/// Só aceita endereço de loopback, para não virar um cliente HTTP genérico dentro do container.
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--health-probe";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static bool IsRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], Argument, StringComparison.Ordinal);
    }

    /// <summary>Executa a sonda; <paramref name="handler"/> existe para os testes.</summary>
    public static async Task<int> RunAsync(string[] args, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var target) || !target.IsLoopback || target.Scheme != Uri.UriSchemeHttp)
        {
            await Console.Error.WriteLineAsync($"Uso: {Argument} http://127.0.0.1:<porta>/health/ready").ConfigureAwait(false);
            return 2;
        }

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout;
        try
        {
            using var response = await client.GetAsync(target).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
