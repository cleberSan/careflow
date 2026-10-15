using System.ComponentModel.DataAnnotations;

namespace CareFlow.Triage.Api.Models;

/// <summary>
/// Qual modelo a triagem usa (ADR-0011). <c>Fake</c> é o padrão e nunca chama rede; <c>Recorded</c> reproduz
/// respostas gravadas (CI, avaliação e Painel); <c>Anthropic</c> chama o Claude de verdade e só é ligado com
/// chave, para gravar ou em execução local. A chave vem de <c>ANTHROPIC_API_KEY</c> (variável de ambiente ou
/// secret), nunca de arquivo versionado.
/// </summary>
internal sealed class TriageModelOptions
{
    public const string SectionName = "Triage:Model";

    public const string FakeProvider = "Fake";
    public const string RecordedProvider = "Recorded";
    public const string AnthropicProvider = "Anthropic";

    [Required]
    public string Provider { get; set; } = FakeProvider;

    /// <summary>Diretório das gravações: lido com <c>Recorded</c>, escrito com <c>Anthropic</c> quando <see cref="Record"/>.</summary>
    public string? RecordingsPath { get; set; }

    /// <summary>Com <c>Anthropic</c>, grava cada resposta em <see cref="RecordingsPath"/>.</summary>
    public bool Record { get; set; }

    /// <summary>Nome registrado em cada triagem reproduzida, o mesmo modelo que gerou as gravações.</summary>
    public string RecordedModelName { get; set; } = DefaultModel;

    /// <summary>Modelo da API da Anthropic.</summary>
    [Required]
    public string Model { get; set; } = DefaultModel;

    /// <summary>Teto de tokens de saída por chamada (raciocínio incluído): limita o custo de cada triagem.</summary>
    [Range(256, 8192)]
    public int MaxTokens { get; set; } = 2048;

    /// <summary>Teto de chamadas reais por processo: o limite de gasto da gravação.</summary>
    [Range(1, 1000)]
    public int MaxCalls { get; set; } = 150;

    [Range(typeof(TimeSpan), "00:00:01", "00:01:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    public const string DefaultModel = "claude-haiku-5-5";
}
