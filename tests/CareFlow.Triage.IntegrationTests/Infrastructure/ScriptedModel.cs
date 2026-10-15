using System.Collections.Concurrent;

using CareFlow.Triage.Api.Models;
using CareFlow.Triage.Domain.Engine;

namespace CareFlow.Triage.IntegrationTests.Infrastructure;

/// <summary>
/// Faz o papel do provedor de IA: guarda cada pedido que sairia do serviço e responde o que o teste programar
/// (por padrão, o modelo falso determinístico). É o ponto onde os testes provam o que chega ao provedor.
/// </summary>
internal sealed class ScriptedModel : ITriageModel
{
    private static readonly DeterministicTriageModel Default = new();

    private Func<ModelPrompt, CancellationToken, Task<ModelReply>> _reply = Default.CompleteAsync;

    public string Name => "modelo-de-teste";

    public ConcurrentQueue<ModelPrompt> Prompts { get; } = new();

    public void Reset()
    {
        Prompts.Clear();
        _reply = Default.CompleteAsync;
    }

    public void Reply(Func<ModelPrompt, CancellationToken, Task<ModelReply>> reply) => _reply = reply;

    /// <summary>Responde sempre o mesmo texto, como um modelo comprometido ou mal comportado faria.</summary>
    public void ReplyWith(string text) => _reply = (_, _) => Task.FromResult<ModelReply>(new ModelReply.Completed(text));

    public Task<ModelReply> CompleteAsync(ModelPrompt prompt, CancellationToken cancellationToken)
    {
        Prompts.Enqueue(prompt);
        return _reply(prompt, cancellationToken);
    }
}
