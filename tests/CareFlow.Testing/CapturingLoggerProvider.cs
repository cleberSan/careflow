using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace CareFlow.Testing;

/// <summary>
/// Guarda todas as mensagens de log (em todos os níveis), com valores estruturados, escopos e exceções,
/// para os testes verificarem que nenhum dado pessoal chega aos logs.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyCollection<CapturedLog> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            provider._scopes.ForEachScope((scope, list) => list.Add(scope?.ToString() ?? string.Empty), scopes);

            // Inclui também os valores estruturados, que um exportador OTLP enviaria mesmo fora da mensagem.
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(';', pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;

            provider._entries.Enqueue(new CapturedLog(
                logLevel,
                category,
                formatter(state, exception),
                values,
                string.Join(';', scopes),
                exception?.ToString() ?? string.Empty));
        }
    }
}

/// <summary>Uma linha de log capturada. <see cref="ToString"/> junta todas as partes, para buscas amplas.</summary>
public sealed record CapturedLog(LogLevel Level, string Category, string Message, string Values, string Scopes, string Exception)
{
    /// <summary>O que o próprio código escreveu, sem os escopos herdados do host (como o caminho da requisição).</summary>
    public string OwnContent => $"{Message}|{Values}|{Exception}";

    public override string ToString() => $"{Level}|{Category}|{Message}|{Values}|{Scopes}|{Exception}";
}
