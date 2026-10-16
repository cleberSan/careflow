using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using CareFlow.Contracts.Rights;
using CareFlow.Rights.Domain.Requests;

namespace CareFlow.Rights.Api.Features.Exports;

/// <summary>
/// Versão legível do pacote: uma página HTML sem script, que abre em qualquer navegador sem internet. Todo valor
/// passa por codificação HTML (um nome cadastrado com marcação não vira código), e a política de conteúdo da página
/// bloqueia qualquer recurso externo.
/// </summary>
internal static class PackageReadme
{
    private static readonly Dictionary<string, string> ServiceTitles = new(StringComparer.Ordinal)
    {
        [SubjectRequestServices.Vault] = "Cadastro",
        [SubjectRequestServices.Scheduling] = "Consultas",
        [SubjectRequestServices.Consent] = "Consentimentos",
        [SubjectRequestServices.Notifications] = "Avisos enviados",
        [SubjectRequestServices.Triage] = "Triagens",
        [SubjectRequestServices.Audit] = "Quem acessou seus dados",
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["patientRef"] = "Pseudônimo",
        ["fullName"] = "Nome",
        ["cpf"] = "CPF",
        ["birthDate"] = "Nascimento",
        ["email"] = "E-mail",
        ["phone"] = "Telefone",
        ["registeredAt"] = "Cadastrado em",
        ["updatedAt"] = "Atualizado em",
        ["appointments"] = "Consultas",
        ["professionalId"] = "Profissional",
        ["startsAt"] = "Início",
        ["endsAt"] = "Fim",
        ["status"] = "Situação",
        ["createdAt"] = "Criado em",
        ["cancelledAt"] = "Cancelado em",
        ["cancellationReason"] = "Motivo do cancelamento",
        ["consents"] = "Situação atual",
        ["history"] = "Histórico",
        ["purpose"] = "Finalidade",
        ["legalBasis"] = "Base legal",
        ["termVersion"] = "Versão do termo",
        ["grantedAt"] = "Concedido em",
        ["revokedAt"] = "Revogado em",
        ["change"] = "Mudança",
        ["version"] = "Versão",
        ["occurredAt"] = "Quando",
        ["entries"] = "Registros",
        ["sourceService"] = "Serviço",
        ["actor"] = "Quem",
        ["action"] = "Ação",
        ["contentDigest"] = "Resumo SHA-256 do que saiu",
        ["hash"] = "Elo da cadeia",
        ["sequence"] = "Sequência",
    };

    public static string Render(SubjectRequest request, IReadOnlyDictionary<string, byte[]> sections, DateTimeOffset generatedAt)
    {
        var html = new StringBuilder(16 * 1024);
        html.Append("""
            <!doctype html>
            <html lang="pt-BR">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
            <title>Seus dados no CareFlow</title>
            <style>
            body{font-family:system-ui,sans-serif;max-width:60rem;margin:2rem auto;padding:0 1rem;line-height:1.5;color:#1a1a1a;background:#fff}
            table{border-collapse:collapse;width:100%;margin:.5rem 0 1.5rem;font-size:.9rem}
            th,td{border:1px solid #ccc;padding:.35rem .5rem;text-align:left;vertical-align:top;word-break:break-word}
            th{background:#f2f2f2}dt{font-weight:600}dd{margin:0 0 .5rem}
            @media (prefers-color-scheme:dark){body{color:#eee;background:#121212}th{background:#2a2a2a}th,td{border-color:#444}}
            </style>
            </head>
            <body>
            <h1>Seus dados no CareFlow</h1>
            """);
        html.Append(CultureInfo.InvariantCulture, $"<p>Pedido {Encode(request.Id.ToString())}, gerado em {Encode(generatedAt.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture))}.</p>");
        html.Append("""
            <p>Esta página mostra os dados que a clínica guarda sobre você, por área. O arquivo <code>dados.json</code> tem o mesmo
            conteúdo em formato estruturado, e <code>fhir-bundle.json</code> traz cadastro, consultas e consentimentos no padrão
            FHIR R4, para levar a outro serviço de saúde.</p>
            """);

        foreach (var service in SubjectRequestServices.All)
        {
            if (!sections.TryGetValue(service, out var json))
            {
                continue;
            }

            html.Append(CultureInfo.InvariantCulture, $"<h2>{Encode(ServiceTitles.GetValueOrDefault(service, service))}</h2>");
            using var document = JsonDocument.Parse(json);
            Write(html, document.RootElement);
        }

        html.Append("</body></html>\n");
        return html.ToString();
    }

    private static void Write(StringBuilder html, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                html.Append("<dl>");
                foreach (var property in element.EnumerateObject())
                {
                    html.Append(CultureInfo.InvariantCulture, $"<dt>{Encode(Label(property.Name))}</dt><dd>");
                    Write(html, property.Value);
                    html.Append("</dd>");
                }

                html.Append("</dl>");
                break;
            case JsonValueKind.Array when element.GetArrayLength() == 0:
                html.Append("<p>Nada registrado.</p>");
                break;
            case JsonValueKind.Array when element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object):
                WriteTable(html, element);
                break;
            case JsonValueKind.Array:
                html.Append("<ul>");
                foreach (var item in element.EnumerateArray())
                {
                    html.Append("<li>");
                    Write(html, item);
                    html.Append("</li>");
                }

                html.Append("</ul>");
                break;
            case JsonValueKind.Null:
                html.Append('-');
                break;
            case JsonValueKind.True:
                html.Append("sim");
                break;
            case JsonValueKind.False:
                html.Append("não");
                break;
            default:
                html.Append(Encode(element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText()));
                break;
        }
    }

    private static void WriteTable(StringBuilder html, JsonElement rows)
    {
        var columns = rows.EnumerateArray()
            .SelectMany(row => row.EnumerateObject().Select(property => property.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        html.Append("<table><thead><tr>");
        foreach (var column in columns)
        {
            html.Append(CultureInfo.InvariantCulture, $"<th scope=\"col\">{Encode(Label(column))}</th>");
        }

        html.Append("</tr></thead><tbody>");
        foreach (var row in rows.EnumerateArray())
        {
            html.Append("<tr>");
            foreach (var column in columns)
            {
                html.Append("<td>");
                if (row.TryGetProperty(column, out var value))
                {
                    Write(html, value);
                }
                else
                {
                    html.Append('-');
                }

                html.Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table>");
    }

    private static string Label(string key) => Labels.GetValueOrDefault(key, key);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
