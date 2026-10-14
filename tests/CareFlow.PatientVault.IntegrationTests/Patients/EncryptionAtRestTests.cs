using System.Net;
using System.Text;

using CareFlow.PatientVault.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Npgsql;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Patients;

/// <summary>
/// Lê o banco direto, com o papel de admin (que ignora RLS), como faria quem rouba um backup ou tem
/// acesso indevido ao servidor: os dados pessoais têm de estar ilegíveis.
/// </summary>
public sealed class EncryptionAtRestTests(VaultApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    private HttpClient Client() => api.CreateClient(_clinic, Read, Write);

    [Fact]
    public async Task StoredRow_ContainsNoPersonalDataInClear()
    {
        // Arrange
        var request = NewPatient();
        var (patientRef, _) = await Client().RegisterOkAsync(request);

        // Act
        var row = await ReadRawRowAsync(patientRef);

        // Assert
        var secrets = new[] { request.FullName!, request.Cpf!, request.Email!, request.Phone!, request.BirthDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var secret in secrets)
        {
            foreach (var encoding in new Encoding[] { Encoding.UTF8, Encoding.Unicode, Encoding.Latin1 })
            {
                Assert.False(Contains(row, encoding.GetBytes(secret)), $"Valor em claro encontrado no banco ({encoding.WebName}).");
            }
        }
    }

    [Fact]
    public async Task SameCpfAndName_InTwoPatientsOfOtherClinics_ProduceDifferentCiphertexts()
    {
        // Arrange
        var request = NewPatient();
        var (refA, _) = await Client().RegisterOkAsync(request);
        var (refB, _) = await api.CreateClient(Guid.NewGuid(), Write).RegisterOkAsync(request);

        // Act
        await using var admin = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(DISTINCT full_name_enc) + count(DISTINCT cpf_enc) FROM patients WHERE patient_ref IN (@a, @b)", admin);
        command.Parameters.AddWithValue("a", refA);
        command.Parameters.AddWithValue("b", refB);
        var distinct = (long)(await command.ExecuteScalarAsync(Ct))!;

        // Assert
        Assert.Equal(4, distinct);
    }

    [Fact]
    public async Task TenantKey_IsStoredOnlyWrapped()
    {
        // Arrange
        await Client().RegisterOkAsync(NewPatient());

        // Act
        await using var admin = await OpenAdminAsync();
        await using var command = new NpgsqlCommand("SELECT kek_id, octet_length(wrapped_key) FROM tenant_keys WHERE tenant_id = @t", admin);
        command.Parameters.AddWithValue("t", _clinic);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));

        // Assert: DEK de 32 bytes + versão, nonce e tag do AES-GCM; nunca os 32 bytes crus.
        Assert.Equal("test-1", reader.GetString(0));
        Assert.Equal(32 + 1 + 12 + 16, reader.GetInt32(1));
    }

    [Fact]
    public async Task CiphertextSwappedBetweenPatients_IsDetectedAndNotDisclosed()
    {
        // Arrange: alguém com acesso de escrita ao banco copia o nome cifrado de um paciente para outro.
        var victim = NewPatient();
        var (victimRef, _) = await Client().RegisterOkAsync(victim);
        var (attackerRef, _) = await Client().RegisterOkAsync(NewPatient());
        await using (var admin = await OpenAdminAsync())
        {
            await admin.ExecuteAsync(
                "UPDATE patients SET full_name_enc = (SELECT full_name_enc FROM patients WHERE patient_ref = @victim) WHERE patient_ref = @attacker",
                ("victim", victimRef),
                ("attacker", attackerRef));
        }

        // Act
        var response = await Client().GetAsync($"/v1/patients/{attackerRef}", Ct);

        // Assert: o AAD amarra o valor ao paciente, então a leitura falha em vez de devolver o nome da vítima.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(victim.FullName!, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(api.AdminConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task<byte[]> ReadRawRowAsync(Guid patientRef)
    {
        await using var admin = await OpenAdminAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT convert_to(row_to_json(p)::text, 'UTF8') || full_name_enc || cpf_enc || birth_date_enc
                   || coalesce(email_enc, '') || coalesce(phone_enc, '') || cpf_index
            FROM patients p WHERE patient_ref = @ref
            """,
            admin);
        command.Parameters.AddWithValue("ref", patientRef);
        return (byte[])(await command.ExecuteScalarAsync(Ct))!;
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
