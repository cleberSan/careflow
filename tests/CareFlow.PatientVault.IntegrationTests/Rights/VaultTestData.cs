using CareFlow.PatientVault.Api.Cryptography;
using CareFlow.PatientVault.Api.Infrastructure.Persistence;
using CareFlow.PatientVault.Domain.Patients;
using CareFlow.PatientVault.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace CareFlow.PatientVault.IntegrationTests.Rights;

internal static class VaultTestData
{
    public const string Keys = "vault.keys";

    /// <summary>
    /// Grava um paciente como a versão anterior do cofre gravava: campos cifrados com a chave da clínica (esquema 1),
    /// sem chave própria. É o estado dos registros cadastrados antes da fase 8.
    /// </summary>
    public static Task<Guid> SeedLegacyPatientAsync(this VaultApiFixture api, Guid clinic, string cpf) =>
        api.InClinicAsync(clinic, async services =>
        {
            var db = services.GetRequiredService<VaultDbContext>();
            var key = await services.GetRequiredService<TenantKeyRing>().GetOrCreateCurrentAsync(VaultClient.Ct);
            var now = DateTimeOffset.UtcNow;
            var data = PersonalData.Create(
                SyntheticPatients.FullName(), Cpf.Create(cpf).Value, new DateOnly(1979, 5, 2), SyntheticPatients.Email(), SyntheticPatients.Phone(),
                DateOnly.FromDateTime(now.UtcDateTime)).Value;

            var record = new PatientRecord
            {
                PatientRef = Guid.NewGuid(),
                TenantId = clinic,
                CreatedAt = now,
                UpdatedAt = now,
                FullName = [],
                Cpf = [],
                BirthDate = [],
                KeyScheme = KeyScheme.ClinicKey,
                KeyVersion = key.Version,
                CpfIndex = PatientProtector.IndexCpf(data.Cpf, key),
            };
            PatientProtector.Encrypt(record, data, key);
            db.Patients.Add(record);
            await db.SaveChangesAsync(VaultClient.Ct);
            return record.PatientRef;
        });

    public static async Task<T?> AdminScalarAsync<T>(this VaultApiFixture api, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnectionString);
        await connection.OpenAsync(VaultClient.Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(VaultClient.Ct);
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task AdminExecuteAsync(this VaultApiFixture api, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(api.AdminConnectionString);
        await connection.OpenAsync(VaultClient.Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(VaultClient.Ct);
    }

    public static Task<long> PatientKeyCountAsync(this VaultApiFixture api, Guid patientRef) =>
        api.AdminScalarAsync<long>("SELECT count(*) FROM patient_keys WHERE patient_ref = @p", ("p", patientRef));
}
