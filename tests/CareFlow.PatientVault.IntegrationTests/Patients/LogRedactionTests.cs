using System.Net;

using CareFlow.PatientVault.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Patients;

/// <summary>
/// Percorre todos os fluxos do cofre (sucesso, validação, conflito, concorrência, adulteração) com logs
/// em nível Trace e verifica que nenhum dado pessoal chegou a nenhuma linha de log, de nenhuma biblioteca.
/// O pseudônimo pode aparecer no caminho da URL nos logs de requisição do ASP.NET Core (desligados em
/// produção pelo nível Warning), mas nunca nos logs do próprio CareFlow.
/// </summary>
public sealed class LogRedactionTests(VaultApiFixture api)
{
    [Fact]
    public async Task AllFlows_LeaveNoPersonalDataInLogs()
    {
        // Arrange
        var clinic = Guid.NewGuid();
        var client = api.CreateClient(clinic, Read, Write);
        var patient = NewPatient();
        var other = NewPatient();
        var invalidEmail = $"invalido-{Guid.NewGuid():N}";

        // Act
        var (patientRef, etag) = await client.RegisterOkAsync(patient);
        var (otherRef, _) = await client.RegisterOkAsync(other);
        await client.GetAsync($"/v1/patients/{patientRef}", Ct);
        await client.LookupAsync(patient.Cpf);
        await client.RegisterAsync(NewPatient(patient.Cpf));                       // CPF duplicado
        await client.RegisterAsync(patient with { Cpf = SyntheticPatients.Cpf(), Email = invalidEmail }); // validação
        await client.UpdateAsync(patientRef, new(patient.FullName, patient.BirthDate, patient.Email, null), etag);
        await client.UpdateAsync(patientRef, new(patient.FullName, patient.BirthDate, patient.Email, null), etag); // versão antiga
        await using (var admin = new Npgsql.NpgsqlConnection(api.AdminConnectionString))
        {
            await admin.OpenAsync(Ct);
            await admin.ExecuteAsync(
                "UPDATE patients SET cpf_enc = (SELECT cpf_enc FROM patients WHERE patient_ref = @a) WHERE patient_ref = @b",
                ("a", patientRef),
                ("b", otherRef));
        }

        var tampered = await client.GetAsync($"/v1/patients/{otherRef}", Ct);                    // falha de integridade

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, tampered.StatusCode);
        var cpf = patient.Cpf!;
        var personalData = new[]
        {
            patient.FullName!, cpf, $"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}", patient.Email!, patient.Phone!,
            other.FullName!, other.Cpf!, other.Email!, other.Phone!, invalidEmail,
        };
        var pseudonyms = new[] { patientRef.ToString(), patientRef.ToString("N"), otherRef.ToString(), otherRef.ToString("N") };

        var entries = api.Logs.Entries.ToArray();
        Assert.NotEmpty(entries);
        AssertAbsent(entries.Select(entry => entry.ToString()), personalData);
        AssertAbsent(
            entries
                .Where(entry => entry.Category.StartsWith("CareFlow.", StringComparison.Ordinal)
                    && !entry.Category.StartsWith("CareFlow.Testing.", StringComparison.Ordinal))
                .Select(entry => entry.OwnContent),
            pseudonyms);
    }

    private static void AssertAbsent(IEnumerable<string> entries, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            var leak = entries.FirstOrDefault(entry => entry.Contains(value, StringComparison.OrdinalIgnoreCase));
            Assert.True(leak is null, $"Valor proibido encontrado no log: {leak?[..Math.Min(leak.Length, 160)]}");
        }
    }
}
