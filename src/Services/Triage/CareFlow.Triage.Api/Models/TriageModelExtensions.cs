using Anthropic;

using CareFlow.BuildingBlocks.AspNetCore.Hosting;

using CareFlow.Triage.Api.Common.Observability;
using CareFlow.Triage.Domain.Engine;

using Microsoft.Extensions.Options;

namespace CareFlow.Triage.Api.Models;

internal static class TriageModelExtensions
{
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    public static IServiceCollection AddTriageModel(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<TriageModelOptions>()
            .BindConfiguration(TriageModelOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                o => o.Provider is TriageModelOptions.FakeProvider or TriageModelOptions.RecordedProvider or TriageModelOptions.AnthropicProvider,
                "Triage:Model:Provider deve ser Fake, Recorded ou Anthropic.")
            .Validate(
                o => o.Provider != TriageModelOptions.RecordedProvider || Directory.Exists(o.RecordingsPath),
                "Triage:Model:RecordingsPath precisa apontar para o diretório das gravações.")
            .Validate(
                o => o.Provider != TriageModelOptions.AnthropicProvider || !string.IsNullOrWhiteSpace(configuration[ApiKeyVariable]),
                $"O provedor Anthropic precisa da chave em {ApiKeyVariable}.")
            .Validate(
                o => o.Provider != TriageModelOptions.AnthropicProvider || o.LiveCallsEnabled || !environment.IsInternetFacing(),
                "Na demonstração hospedada e em produção a triagem usa as respostas gravadas; o modelo real exige Triage:Model:LiveCallsEnabled=true (ADR-0013).")
            .Validate(
                o => !o.Record || (o.Provider == TriageModelOptions.AnthropicProvider && !string.IsNullOrWhiteSpace(o.RecordingsPath)),
                "Triage:Model:Record só vale com o provedor Anthropic e um RecordingsPath.")
            .ValidateOnStart();

        services.AddSingleton<ITriageModel>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TriageModelOptions>>().Value;
            var time = provider.GetRequiredService<TimeProvider>();
            ITriageModel inner = options.Provider switch
            {
                TriageModelOptions.RecordedProvider => new RecordedTriageModel(options.RecordingsPath!, options.RecordedModelName),
                TriageModelOptions.AnthropicProvider => Live(provider, options, configuration[ApiKeyVariable]!, time),
                _ => new DeterministicTriageModel(),
            };
            return new ObservedTriageModel(
                inner,
                options.Timeout,
                provider.GetRequiredService<TriageMetrics>(),
                time,
                provider.GetRequiredService<ILogger<ObservedTriageModel>>());
        });
        return services;
    }

    private static RecordingTriageModel Live(IServiceProvider provider, TriageModelOptions options, string apiKey, TimeProvider time)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        ITriageModel live = new AnthropicTriageModel(client, options.Model, options.MaxTokens, provider.GetRequiredService<ILogger<AnthropicTriageModel>>());
        return new RecordingTriageModel(live, options.Record ? options.RecordingsPath : null, options.MaxCalls, time);
    }
}
