using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace CareFlow.Testing;

/// <summary>
/// Guarda todas as mensagens de log (em todos os níveis), com exceções e escopos, para os testes
/// verificarem que nenhum dado pessoal chega aos logs.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<string> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyCollection<string> Entries => _entries;

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

            provider._entries.Enqueue(
                $"{logLevel}|{category}|{formatter(state, exception)}|{values}|{string.Join(';', scopes)}|{exception}");
        }
    }
}
