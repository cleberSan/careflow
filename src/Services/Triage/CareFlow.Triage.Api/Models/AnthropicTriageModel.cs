using System.Text.Json;

using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.Domain.Suggestions;

namespace CareFlow.Triage.Api.Models;

/// <summary>
/// Claude pela API da Anthropic, com saída estruturada no schema da sugestão (ADR-0011). Só é usado para gravar
/// respostas (workflow manual com teto de gasto) e em execução local com chave; o CI e o Painel reproduzem as
/// gravações. Envia as instruções fixas como system e o caso pseudonimizado como única mensagem do usuário, sem
/// identificador nenhum. Falha do provedor vira "indisponível": a triagem segue só com as regras.
/// </summary>
internal sealed partial class AnthropicTriageModel(AnthropicClient client, string model, int maxTokens, ILogger<AnthropicTriageModel> logger)
    : ITriageModel
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Schema =
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SuggestionSchema.JsonSchema)!;

    public string Name => model;

    public async Task<ModelReply> CompleteAsync(ModelPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        Message response;
        try
        {
            response = await client.Messages.Create(
                new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = maxTokens,
                    System = prompt.Instructions,
                    Messages = [new() { Role = Role.User, Content = prompt.Payload }],
                    OutputConfig = new OutputConfig
                    {
                        // Classificação curta: pouco raciocínio basta e mantém custo e latência baixos.
                        Effort = Effort.Low,
                        Format = new JsonOutputFormat { Schema = Schema },
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AnthropicException exception)
        {
            LogProviderError(logger, model, exception.GetType().Name);
            return new ModelReply.Unavailable("erro-do-provedor");
        }

        var stopReason = response.StopReason?.Raw();
        if (string.Equals(stopReason, "refusal", StringComparison.Ordinal))
        {
            return new ModelReply.Refused();
        }

        if (string.Equals(stopReason, "max_tokens", StringComparison.Ordinal))
        {
            return new ModelReply.Unavailable("resposta-truncada");
        }

        var text = string.Concat(response.Content.Select(block => block.Value).OfType<TextBlock>().Select(block => block.Text));
        return new ModelReply.Completed(text);
    }

    [LoggerMessage(EventId = 7202, Level = LogLevel.Warning, Message = "Provedor do modelo {Model} recusou a chamada com {Error}")]
    private static partial void LogProviderError(ILogger logger, string model, string error);
}
