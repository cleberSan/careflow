using System.Net;
using System.Net.Http.Json;

using CareFlow.PatientVault.Api.Features.Keys;
using CareFlow.PatientVault.Api.Features.Patients;
using CareFlow.PatientVault.IntegrationTests.Infrastructure;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Rights;

/// <summary>
/// Migração da chave da clínica para a chave por paciente e rotação da chave da clínica, com o cofre no ar: as
/// leituras funcionam antes, durante e depois de cada passo (ADR-0012).
/// </summary>
public sealed class KeyRotationTests(VaultApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    private HttpClient Admin => api.CreateClient(_clinic, VaultTestData.Keys);

    private HttpClient Reader => api.CreateClient(_clinic, Read, Write);

    private async Task<KeyMaintenanceResult> MaintainAsync()
    {
        var response = await Admin.PostAsync("/v1/keys/maintenance?batchSize=1", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<KeyMaintenanceResult>(Json, Ct))!;
    }

    private async Task<PatientResponse> GetAsync(Guid patientRef)
    {
        var response = await Reader.GetAsync($"/v1/patients/{patientRef}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PatientResponse>(Json, Ct))!;
    }

    [Fact]
    public async Task LegacyRecord_IsReadable_ThenMigratesToItsOwnKey_WithoutChangingData()
    {
        // Arrange
        var cpf = Testing.SyntheticPatients.Cpf();
        var patientRef = await api.SeedLegacyPatientAsync(_clinic, cpf);
        var before = await GetAsync(patientRef);

        // Act
        var batch = await MaintainAsync();

        // Assert
        Assert.Equal(1, batch.Migrated);
        Assert.Equal(0, batch.Remaining);
        Assert.Equal(1, await api.PatientKeyCountAsync(patientRef));
        Assert.Equal((short)2, await api.AdminScalarAsync<short>("SELECT key_scheme FROM patients WHERE patient_ref = @p", ("p", patientRef)));
        var after = await GetAsync(patientRef);
        Assert.Equal(before with { UpdatedAt = after.UpdatedAt }, after);
        Assert.Equal(HttpStatusCode.OK, (await Reader.LookupAsync(cpf)).StatusCode);
    }

    [Fact]
    public async Task Rotation_KeepsEveryRecordReadable_AndOldVersionIsDestroyedOnlyWhenUnused()
    {
        // Arrange: um registro antigo e um novo, na versão 1.
        var legacyCpf = Testing.SyntheticPatients.Cpf();
        var legacy = await api.SeedLegacyPatientAsync(_clinic, legacyCpf);
        var request = NewPatient();
        var (current, _) = await Reader.RegisterOkAsync(request);

        // Act 1: rotação. A versão 1 ainda protege os dois registros.
        var rotation = await Admin.PostAsync("/v1/keys/rotation", null, Ct);
        var refused = await Admin.DeleteAsync("/v1/keys/1", Ct);

        // Assert 1
        Assert.Equal(HttpStatusCode.Created, rotation.StatusCode);
        Assert.Equal(2, (await rotation.Content.ReadFromJsonAsync<KeyRotationResponse>(Json, Ct))!.Version);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Chave.EmUso", await refused.ReadProblemCodeAsync());
        await GetAsync(legacy);
        await GetAsync(current);
        Assert.Equal(HttpStatusCode.OK, (await Reader.LookupAsync(request.Cpf)).StatusCode);

        // Act 2: manutenção em lotes de 1 até não restar nada.
        var first = await MaintainAsync();
        var second = await MaintainAsync();
        var third = await MaintainAsync();

        // Assert 2
        Assert.Equal(1, first.Remaining);
        Assert.Equal(0, second.Remaining);
        Assert.Equal(2, first.Migrated + first.Rewrapped + second.Migrated + second.Rewrapped);
        Assert.Equal(0, third.Migrated + third.Rewrapped);

        // Act 3: a versão 1 sai; a atual não pode sair.
        var destroyed = await Admin.DeleteAsync("/v1/keys/1", Ct);
        var currentRefused = await Admin.DeleteAsync("/v1/keys/2", Ct);
        var missing = await Admin.DeleteAsync("/v1/keys/1", Ct);

        // Assert 3: tudo continua legível e encontrável pelo CPF, só com a versão 2.
        Assert.Equal(HttpStatusCode.NoContent, destroyed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, currentRefused.StatusCode);
        Assert.Equal("Chave.Atual", await currentRefused.ReadProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(1L, await api.AdminScalarAsync<long>("SELECT count(*) FROM tenant_keys WHERE tenant_id = @t", ("t", _clinic)));
        await GetAsync(legacy);
        await GetAsync(current);
        Assert.Equal(HttpStatusCode.OK, (await Reader.LookupAsync(legacyCpf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Reader.LookupAsync(request.Cpf)).StatusCode);
    }

    [Fact]
    public async Task DuringRotation_SameCpfInAnotherVersion_IsStillADuplicate()
    {
        // Arrange: cadastrado na versão 1; a rotação muda a chave do blind index.
        var request = NewPatient();
        await Reader.RegisterOkAsync(request);
        await Admin.PostAsync("/v1/keys/rotation", null, Ct);

        // Act
        var duplicate = await Reader.RegisterAsync(NewPatient(request.Cpf));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task KeyEndpoints_RequireTheKeysScope_AndPatientScopesDoNotGrantIt()
    {
        // Act
        var rotation = await Reader.PostAsync("/v1/keys/rotation", null, Ct);
        var maintenance = await Reader.PostAsync("/v1/keys/maintenance", null, Ct);
        var destroy = await Reader.DeleteAsync("/v1/keys/1", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, rotation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, maintenance.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, destroy.StatusCode);
    }

    [Fact]
    public async Task Rotation_IsAudited_WithoutPersonalData()
    {
        // Act
        await Admin.PostAsync("/v1/keys/rotation", null, Ct);

        // Assert
        var outbox = await api.OutboxAsync(_clinic);
        var entry = Assert.Single(outbox, row => row.AuditAction == "tenant-key.rotated");
        Assert.Equal("tenant-key", entry.Payload.GetProperty("resourceType").GetString());
    }
}
