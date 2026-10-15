using System.Net;

using CareFlow.PatientVault.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Patients;

/// <summary>Isolamento entre clínicas em cada endpoint do cofre: outra clínica nunca vê nem altera.</summary>
public sealed class ClinicIsolationTests(VaultApiFixture api)
{
    private readonly Guid _clinicA = Guid.NewGuid();
    private readonly Guid _clinicB = Guid.NewGuid();

    private HttpClient ClinicA() => api.CreateClient(_clinicA, Read, Write);

    private HttpClient ClinicB() => api.CreateClient(_clinicB, Read, Write);

    [Fact]
    public async Task Get_PatientOfAnotherClinic_ReturnsNotFound()
    {
        // Arrange
        var (patientRef, _) = await ClinicA().RegisterOkAsync(NewPatient());

        // Act
        var response = await ClinicB().GetAsync($"/v1/patients/{patientRef}", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Paciente.NaoEncontrado", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Update_PatientOfAnotherClinic_ReturnsNotFoundAndKeepsData()
    {
        // Arrange
        var request = NewPatient();
        var (patientRef, etag) = await ClinicA().RegisterOkAsync(request);

        // Act: mesmo com o ETag correto, a outra clínica não encontra o paciente.
        var response = await ClinicB().UpdateAsync(
            patientRef,
            new(SyntheticPatients.FullName(), new DateOnly(1990, 1, 1), null, null),
            etag);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var stillThere = await ClinicA().GetAsync($"/v1/patients/{patientRef}", Ct);
        Assert.Equal(etag, stillThere.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Lookup_CpfRegisteredOnlyInAnotherClinic_ReturnsNotFound()
    {
        // Arrange
        var cpf = SyntheticPatients.Cpf();
        await ClinicA().RegisterOkAsync(NewPatient(cpf));
        await ClinicB().RegisterOkAsync(NewPatient());

        // Act
        var response = await ClinicB().LookupAsync(cpf);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Register_SameCpfInAnotherClinic_CreatesUnlinkablePseudonym()
    {
        // Arrange
        var cpf = SyntheticPatients.Cpf();
        var (refInA, _) = await ClinicA().RegisterOkAsync(NewPatient(cpf));

        // Act
        var (refInB, _) = await ClinicB().RegisterOkAsync(NewPatient(cpf));

        // Assert: pseudônimos diferentes e blind index diferente; nada liga os dois cadastros.
        Assert.NotEqual(refInA, refInB);
        await using var admin = new Npgsql.NpgsqlConnection(api.AdminConnectionString);
        await admin.OpenAsync(Ct);
        var distinctIndexes = await admin.CountAsync(
            "SELECT count(DISTINCT cpf_index) FROM patients WHERE patient_ref IN (@a, @b)", ("a", refInA), ("b", refInB));
        Assert.Equal(2, distinctIndexes);
    }
}
