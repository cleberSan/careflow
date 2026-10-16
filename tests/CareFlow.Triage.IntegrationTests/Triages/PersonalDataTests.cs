using System.Text.RegularExpressions;

using CareFlow.Testing;
using CareFlow.Triage.IntegrationTests.Infrastructure;

using static CareFlow.Triage.IntegrationTests.Infrastructure.TriageClient;

namespace CareFlow.Triage.IntegrationTests.Triages;

/// <summary>
/// Queixas sintéticas recheadas de dados pessoais: nenhum deles chega ao provedor de IA, aos logs, aos spans, às
/// métricas ou à trilha de auditoria. A queixa original só existe no banco do Triage, para quem revisa.
/// </summary>
public sealed class PersonalDataTests
{
    private readonly TriageApiFixture _api;
    private readonly Guid _clinic = Guid.NewGuid();

    public PersonalDataTests(TriageApiFixture api)
    {
        _api = api;
        api.Reset();
    }

    private const string Symptoms = " Estou com tosse seca, cansaço e um pouco de dor no corpo há três dias, pior à noite e ao acordar.";

    private sealed record Stuffed(string Complaint, string[] Secrets);

    private static IEnumerable<Stuffed> Complaints(string fullName)
    {
        var first = fullName.Split(' ')[0];
        var last = fullName.Split(' ')[^1];
        var cpf = SyntheticPatients.Cpf(formatted: true);
        var cpfDigits = SyntheticPatients.Cpf();
        var email = SyntheticPatients.Email();
        var phone = SyntheticPatients.Phone();
        var localPhone = $"(11) 9{phone[^8..^4]}-{phone[^4..]}";

        yield return new($"Meu nome é {fullName}, CPF {cpf}, estou com tosse há três dias.", [fullName, last, cpf, cpf.Replace(".", "", StringComparison.Ordinal)]);
        yield return new($"Sou {first} {last}, cpf {cpfDigits}, e-mail {email}, febre desde ontem.", [last, cpfDigits, email, email.Split('@')[0]]);
        yield return new($"Liga pra mim no {localPhone} ou +{phone}, dor de cabeça forte.", [localPhone, phone, phone[^8..]]);
        yield return new($"Nasci em 14/03/1987 e moro na Rua das Acácias, 1234, apto 56. Dor nas costas.", ["14/03/1987", "1987", "Acácias", "1234"]);
        yield return new($"Fui atendido dia 3 de outubro, meu instagram é @{last.ToLowerInvariant()}_saude e o site https://exemplo.test/{last}.", [$"@{last.ToLowerInvariant()}_saude", "exemplo.test", "3 de outubro"]);
        yield return new($"Tenho 47 anos, prontuário 99887766, convênio 123456789012. Tontura.", ["47 anos", "99887766", "123456789012"]);
        yield return new($"A {last.ToUpperInvariant()} aqui, minha mãe {first} pediu pra eu escrever: vômito desde cedo.", [last.ToUpperInvariant(), $"mãe {first}"]);
    }

    [Fact]
    public async Task StuffedComplaints_ReachNeitherTheProviderNorTelemetryNorAudit()
    {
        // Arrange
        var fullName = $"Joana {SyntheticPatients.FullName().Split(' ')[^1]} Albuquerque";
        var patient = await _api.ConsentingPatientAsync(_clinic, age: 38, fullName: fullName);
        var client = _api.CreateClient(_clinic, Submit, Read);
        // Cada queixa ganha sintomas suficientes para não ser barrada por excesso de mascaramento e chegar ao modelo.
        var cases = Complaints(fullName).Select(c => c with { Complaint = c.Complaint + Symptoms }).ToArray();
        var responses = new List<Api.Features.Triages.TriageResponse>();

        // Act
        foreach (var stuffed in cases)
        {
            var created = await client.SubmitOkAsync(patient, stuffed.Complaint);
            responses.Add(await client.GetOkAsync(created.Id));
        }

        // Assert: o que saiu é a única coisa que o provedor vê.
        var outbound = _api.Model.Prompts.Select(p => p.Instructions + "\n" + p.Payload).ToArray();
        Assert.All(responses, r => Assert.Equal("accepted", r.ModelStatus));
        Assert.Equal(cases.Length, outbound.Length);
        var logs = _api.Logs.Entries.Select(entry => entry.OwnContent + "|" + entry.Scopes).ToArray();
        var telemetry = _api.Telemetry.All.ToArray();
        var audit = (await _api.OutboxAsync(_clinic)).Select(row => row.Payload.GetRawText()).ToArray();

        foreach (var (stuffed, response) in cases.Zip(responses))
        {
            Assert.Equal(stuffed.Complaint, response.Complaint);
            Assert.NotNull(response.SentToModel);
            foreach (var secret in stuffed.Secrets.Append(fullName))
            {
                Assert.DoesNotContain(secret, response.SentToModel, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(outbound, text => text.Contains(secret, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(outbound, text => text.Contains(System.Text.Json.JsonSerializer.Serialize(secret)[1..^1], StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(logs, text => Leaks(text, secret));
                Assert.DoesNotContain(telemetry, text => Leaks(text, secret));
                Assert.DoesNotContain(audit, text => Leaks(text, secret));
            }
        }

        // Nem o pseudônimo nem a clínica saem para o provedor, e a queixa não aparece em logs nem spans.
        foreach (var identifier in new[] { patient.ToString(), patient.ToString("N"), _clinic.ToString(), _clinic.ToString("N") })
        {
            Assert.DoesNotContain(outbound, text => text.Contains(identifier, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(logs, text => text.Contains(identifier, StringComparison.OrdinalIgnoreCase));
        }

        Assert.DoesNotContain(logs, text => text.Contains("tosse", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(telemetry, text => text.Contains("tosse", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(telemetry, text => text.Contains("call triage model", StringComparison.Ordinal));
    }

    /// <summary>
    /// Em logs, spans e auditoria há identificadores aleatórios em hexadecimal (TraceId, SpanId, hashes): "1234" dentro
    /// deles não é o número da casa do paciente. Vazamento ali é o segredo como termo inteiro. O que vai ao provedor
    /// não tem esses identificadores e continua sendo verificado como substring.
    /// </summary>
    private static bool Leaks(string text, string secret) =>
        Regex.IsMatch(text, $"(?<![0-9A-Za-z]){Regex.Escape(secret)}(?![0-9A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task MaskCounts_ArePublished_ButNeverTheMaskedValues()
    {
        // Arrange
        var patient = await _api.ConsentingPatientAsync(_clinic, fullName: "Rafael Sintetico Moura");

        // Act
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(patient, "Sou o Rafael Moura, CPF 123.456.789-09, tel (11) 98765-4321, com tosse seca há três dias e cansaço.");

        // Assert
        // Nome e sobrenome seguidos viram um marcador só: nem a quantidade de palavras do nome sai.
        Assert.Equal("Sou o [nome], CPF [cpf], tel [telefone], com tosse seca há três dias e cansaço.", triage.SentToModel);
        Assert.Equal(1, triage.Masks["name"]);
        Assert.Equal(1, triage.Masks["cpf"]);
        Assert.Equal(1, triage.Masks["phone"]);
    }

    [Fact]
    public async Task OverMaskedComplaint_NeverLeaves_AndGoesToTheProfessional()
    {
        // Arrange: quase tudo é identificador; o que sobraria não serve para triar e ainda exporia contexto.
        var patient = await _api.ConsentingPatientAsync(_clinic, fullName: "Bruna Sintetica Lopes");

        // Act
        var triage = await _api.CreateClient(_clinic, Submit).SubmitOkAsync(
            patient, "Bruna Lopes 123.456.789-09 bruna.lopes@example.com (11) 98765-4321 14/03/1987 dor");

        // Assert
        Assert.Empty(_api.Model.Prompts);
        Assert.Null(triage.SentToModel);
        Assert.Equal("skipped-over-masked", triage.ModelStatus);
        Assert.Contains(triage.Guardrails, g => g.StartsWith("masking:", StringComparison.Ordinal));
    }
}
