using System.Diagnostics;
using System.Text.RegularExpressions;

using OpenTelemetry;
using OpenTelemetry.Logs;

namespace CareFlow.BuildingBlocks.AspNetCore.Observability;

/// <summary>
/// Última barreira antes do exportador. O código do CareFlow já não escreve dado pessoal em logs nem em spans
/// (os testes conferem); aqui ficam de fora o que vem de bibliotecas: identificadores em caminhos de URL e
/// qualquer coisa com forma de e-mail, CPF ou telefone. A garantia de verdade é o teste de ponta a ponta que
/// varre a telemetria exportada; isto reduz o estrago se um dia algo escapar.
/// </summary>
public static partial class TelemetryRedaction
{
    public const string IdPlaceholder = "{id}";

    /// <summary>Troca GUIDs (pseudônimos, clínicas, recursos) por <c>{id}</c> e tira a query string.</summary>
    public static string MaskUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var query = value.IndexOf('?', StringComparison.Ordinal);
        var withoutQuery = query >= 0 ? value[..query] : value;
        return GuidPattern().Replace(withoutQuery, IdPlaceholder);
    }

    public static string MaskIdentifiers(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return GuidPattern().Replace(value, IdPlaceholder);
    }

    /// <summary>E-mail, CPF (com ou sem pontuação) e telefone brasileiro viram marcadores.</summary>
    public static string ScrubPersonalData(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length < 8)
        {
            return value;
        }

        var scrubbed = EmailPattern().Replace(value, "[email]");
        scrubbed = PhonePattern().Replace(scrubbed, "[telefone]");
        return CpfPattern().Replace(scrubbed, "[cpf]");
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\dA-Fa-f-])\d{3}\.?\d{3}\.?\d{3}-?\d{2}(?![\dA-Fa-f-])", RegexOptions.CultureInvariant)]
    private static partial Regex CpfPattern();

    [GeneratedRegex(@"(?<![\dA-Fa-f-])\+?55[\s(]*\d{2}[\s)]*9?\d{4}[\s-]?\d{4}(?![\dA-Fa-f-])", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();
}

/// <summary>
/// Spans: o caminho da requisição sai como o template da rota (<c>/v1/patients/{patientRef}</c>), URLs de saída
/// sem identificadores nem query string, e atributos de texto passam pelo filtro de dados pessoais.
/// </summary>
public sealed class SpanRedactionProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var route = data.GetTagItem("http.route") as string;
        List<KeyValuePair<string, string?>>? changes = null;
        foreach (var tag in data.EnumerateTagObjects())
        {
            if (tag.Value is not string text)
            {
                continue;
            }

            var redacted = tag.Key switch
            {
                "url.path" => string.IsNullOrEmpty(route) ? TelemetryRedaction.MaskUrl(text) : route,
                "url.full" => TelemetryRedaction.MaskUrl(text),
                "url.query" => null,
                _ => TelemetryRedaction.ScrubPersonalData(text),
            };

            if (!string.Equals(redacted, text, StringComparison.Ordinal))
            {
                (changes ??= []).Add(new(tag.Key, redacted));
            }
        }

        if (changes is not null)
        {
            foreach (var (key, value) in changes)
            {
                data.SetTag(key, value);
            }
        }

        if (data.StatusDescription is { } description)
        {
            var scrubbed = TelemetryRedaction.ScrubPersonalData(description);
            if (!string.Equals(scrubbed, description, StringComparison.Ordinal))
            {
                data.SetStatus(data.Status, scrubbed);
            }
        }

        // Uma requisição de cliente com nome livre (por exemplo, "GET /v1/patients/<guid>") vira o template.
        if (data.DisplayName.Contains('/', StringComparison.Ordinal))
        {
            data.DisplayName = TelemetryRedaction.MaskUrl(data.DisplayName);
        }
    }
}

/// <summary>
/// Logs exportados: mensagem e atributos de texto passam pelo filtro de dados pessoais; nos logs de
/// bibliotecas (fora de <c>CareFlow.*</c>), identificadores também são mascarados, porque o ASP.NET Core e o
/// HttpClient registram caminhos completos.
/// </summary>
public sealed class LogRedactionProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var framework = data.CategoryName is null || !data.CategoryName.StartsWith("CareFlow.", StringComparison.Ordinal);
        if (data.FormattedMessage is { } message)
        {
            data.FormattedMessage = Redact(message, framework);
        }

        if (data.Body is { } body)
        {
            data.Body = Redact(body, framework);
        }

        if (data.Attributes is { Count: > 0 } attributes)
        {
            var redacted = new List<KeyValuePair<string, object?>>(attributes.Count);
            var changed = false;
            foreach (var attribute in attributes)
            {
                if (attribute.Value is string text)
                {
                    var value = Redact(text, framework);
                    changed |= !string.Equals(value, text, StringComparison.Ordinal);
                    redacted.Add(new(attribute.Key, value));
                }
                else
                {
                    redacted.Add(attribute);
                }
            }

            if (changed)
            {
                data.Attributes = redacted;
            }
        }
    }

    private static string Redact(string value, bool framework)
    {
        var scrubbed = TelemetryRedaction.ScrubPersonalData(value);
        return framework ? TelemetryRedaction.MaskIdentifiers(scrubbed) : scrubbed;
    }
}
