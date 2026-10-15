using System.Diagnostics;

using CareFlow.Triage.Api.Common.Observability;
using CareFlow.Triage.Domain.Engine;

namespace CareFlow.Triage.Api.Models;

/// <summary>
/// Em volta de qualquer modelo: prazo máximo, span, métrica de duração e erro do provedor virando
/// "indisponível" (a triagem segue com as regras). O span leva só o nome do modelo, a versão do pedido e o
/// resultado; nunca o texto enviado ou recebido.
/// </summary>
internal sealed partial class ObservedTriageModel(ITriageModel inner, TimeSpan timeout, TriageMetrics metrics, TimeProvider time, ILogger<ObservedTriageModel> logger)
    : ITriageModel
{
    public string Name => inner.Name;

    public async Task<ModelReply> CompleteAsync(ModelPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        using var activity = TriageDiagnostics.ActivitySource.StartActivity("call triage model", ActivityKind.Client);
        activity?.SetTag("gen_ai.request.model", inner.Name);
        activity?.SetTag("careflow.triage.prompt_version", prompt.Version);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var started = time.GetTimestamp();
        ModelReply reply;
        try
        {
            reply = await inner.CompleteAsync(prompt, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reply = new ModelReply.Unavailable("timeout");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            LogProviderError(logger, inner.Name, exception.GetType().Name);
            reply = new ModelReply.Unavailable("erro-do-provedor");
        }

        var outcome = reply switch
        {
            ModelReply.Completed => "completed",
            ModelReply.Refused => "refused",
            ModelReply.Unavailable unavailable => unavailable.Reason,
            _ => "unknown",
        };
        activity?.SetTag("careflow.triage.model_outcome", outcome);
        if (reply is ModelReply.Unavailable)
        {
            activity?.SetStatus(ActivityStatusCode.Error, outcome);
        }

        metrics.ModelCall(time.GetElapsedTime(started), outcome);
        return reply;
    }

    [LoggerMessage(EventId = 7201, Level = LogLevel.Warning, Message = "Modelo {Model} falhou com {Error}; a triagem segue só com as regras")]
    private static partial void LogProviderError(ILogger logger, string model, string error);
}
