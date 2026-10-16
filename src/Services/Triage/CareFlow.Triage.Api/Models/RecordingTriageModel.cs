using System.Text.Json;

using CareFlow.Triage.Domain.Engine;

namespace CareFlow.Triage.Api.Models;

/// <summary>
/// Em volta do modelo real: limita o gasto (chamadas além de <paramref name="maxCalls"/> não saem e respondem
/// "indisponível") e, com <paramref name="directory"/>, grava a resposta (concluída ou recusada) como
/// <see cref="ModelCassette"/>, uma por digest. Falhas do provedor não são gravadas: a próxima gravação tenta de novo.
/// </summary>
internal sealed class RecordingTriageModel(ITriageModel inner, string? directory, int maxCalls, TimeProvider time) : ITriageModel
{
    public const string BudgetExhausted = "orcamento-esgotado";

    private int _calls;

    public string Name => inner.Name;

    public int Calls => Volatile.Read(ref _calls);

    public async Task<ModelReply> CompleteAsync(ModelPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (Interlocked.Increment(ref _calls) > maxCalls)
        {
            return new ModelReply.Unavailable(BudgetExhausted);
        }

        var reply = await inner.CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (directory is not null && ModelCassette.FromReply(prompt, inner.Name, time.GetUtcNow(), reply) is { } cassette)
        {
            Directory.CreateDirectory(directory);
            var stream = File.Create(Path.Combine(directory, ModelCassette.FileName(prompt.Digest)));
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, cassette, ModelCassette.Json, cancellationToken).ConfigureAwait(false);
            }
        }

        return reply;
    }
}
