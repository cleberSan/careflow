using System.Net;
using System.Net.Http.Json;

using CareFlow.PatientVault.Api.Features.Patients;
using CareFlow.PatientVault.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Patients;

public sealed class PatientLifecycleTests(VaultApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    private HttpClient Client() => api.CreateClient(_clinic, Read, Write);

    [Fact]
    public async Task Register_ReturnsOnlyPseudonymWithLocationETagAndNoStore()
    {
        // Arrange
        var request = NewPatient();

        // Act
        var response = await Client().RegisterAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var created = await response.Content.ReadFromJsonAsync<PatientRefResponse>(Json, Ct);
        Assert.Equal($"/v1/patients/{created!.PatientRef}", response.Headers.Location?.ToString());
        Assert.NotNull(response.Headers.ETag);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(request.Cpf!, body, StringComparison.Ordinal);
        Assert.DoesNotContain(request.FullName!, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ReturnsDecryptedNormalizedData()
    {
        // Arrange
        var cpf = SyntheticPatients.Cpf();
        var request = NewPatient($"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}");
        var (patientRef, etag) = await Client().RegisterOkAsync(request);

        // Act
        var response = await Client().GetAsync($"/v1/patients/{patientRef}", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(etag, response.Headers.ETag?.Tag);
        var patient = await response.Content.ReadFromJsonAsync<PatientResponse>(Json, Ct);
        Assert.Equal(patientRef, patient!.PatientRef);
        Assert.Equal(request.FullName, patient.FullName);
        Assert.Equal(cpf, patient.Cpf);
        Assert.Equal(request.BirthDate, patient.BirthDate);
        Assert.Equal(request.Email, patient.Email);
        Assert.Equal(request.Phone, patient.Phone);
    }

    [Fact]
    public async Task Get_Unknown_ReturnsNotFound()
    {
        // Act
        var response = await Client().GetAsync($"/v1/patients/{Guid.NewGuid()}", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Paciente.NaoEncontrado", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Register_InvalidCpf_ReturnsValidationProblemWithoutEchoingValue()
    {
        // Arrange
        var request = NewPatient(cpf: "12345678900");

        // Act
        var response = await Client().RegisterAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Paciente.CpfInvalido", await response.ReadProblemCodeAsync());
        Assert.DoesNotContain("12345678900", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_InvalidPersonalData_ReturnsValidationProblem()
    {
        // Arrange
        var request = NewPatient() with { Email = "invalido" };

        // Act
        var response = await Client().RegisterAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Paciente.EmailInvalido", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Register_WithoutOptionalContacts_Succeeds()
    {
        // Arrange
        var request = NewPatient() with { Email = null, Phone = null };
        var (patientRef, _) = await Client().RegisterOkAsync(request);

        // Act
        var patient = await Client().GetFromJsonAsync<PatientResponse>($"/v1/patients/{patientRef}", Json, Ct);

        // Assert
        Assert.Null(patient!.Email);
        Assert.Null(patient.Phone);
    }

    [Fact]
    public async Task Register_DuplicateCpfInSameClinic_ReturnsConflict()
    {
        // Arrange
        var cpf = SyntheticPatients.Cpf();
        await Client().RegisterOkAsync(NewPatient(cpf));

        // Act: mesmo CPF com outra formatação continua sendo o mesmo paciente.
        var response = await Client().RegisterAsync(NewPatient($"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Paciente.CpfJaCadastrado", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Register_ConcurrentSameCpf_ExactlyOneSucceeds()
    {
        // Arrange
        var cpf = SyntheticPatients.Cpf();
        var client = Client();

        // Act: clínica nova, então as requisições também disputam a criação da chave da clínica.
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.RegisterAsync(NewPatient(cpf))));

        // Assert
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Register_ConcurrentFirstPatientsOfNewClinic_ShareOneClinicKey()
    {
        // Arrange
        var client = Client();

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.RegisterAsync(NewPatient())));

        // Assert
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinic);
        Assert.Equal(1, await connection.CountAsync("SELECT count(*) FROM tenant_keys"));
        Assert.Equal(8, await connection.CountAsync("SELECT count(*) FROM patients"));
    }

    [Fact]
    public async Task Update_WithCurrentETag_ChangesDataAndReturnsNewETag()
    {
        // Arrange
        var request = NewPatient();
        var (patientRef, etag) = await Client().RegisterOkAsync(request);
        var update = new UpdatePatientRequest("Nome Sintetico Alterado", new DateOnly(1988, 3, 10), SyntheticPatients.Email(), null);

        // Act
        var response = await Client().UpdateAsync(patientRef, update, etag);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotEqual(etag, response.Headers.ETag?.Tag);
        var patient = await Client().GetFromJsonAsync<PatientResponse>($"/v1/patients/{patientRef}", Json, Ct);
        Assert.Equal(update.FullName, patient!.FullName);
        Assert.Equal(update.BirthDate, patient.BirthDate);
        Assert.Equal(update.Email, patient.Email);
        Assert.Null(patient.Phone);
        Assert.Equal(request.Cpf, patient.Cpf);
        Assert.True(patient.UpdatedAt >= patient.CreatedAt);
    }

    [Fact]
    public async Task Update_WithoutIfMatch_ReturnsPreconditionRequired()
    {
        // Arrange
        var (patientRef, _) = await Client().RegisterOkAsync(NewPatient());

        // Act
        var response = await Client().UpdateAsync(patientRef, Change(), ifMatch: null);

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal("Paciente.VersaoObrigatoria", await response.ReadProblemCodeAsync());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("\"1\"")]
    public async Task Update_WithStaleOrWildcardETag_ReturnsPreconditionFailed(string ifMatch)
    {
        // Arrange
        var (patientRef, _) = await Client().RegisterOkAsync(NewPatient());

        // Act
        var response = await Client().UpdateAsync(patientRef, Change(), ifMatch);

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("Paciente.VersaoDesatualizada", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Update_ConcurrentWithSameETag_OnlyOneWins()
    {
        // Arrange
        var (patientRef, etag) = await Client().RegisterOkAsync(NewPatient());
        var client = Client();

        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.UpdateAsync(patientRef, Change(), etag)));

        // Assert
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.All(
            responses.Where(r => r.StatusCode != HttpStatusCode.NoContent),
            r => Assert.Equal(HttpStatusCode.PreconditionFailed, r.StatusCode));
    }

    [Fact]
    public async Task Update_InvalidData_ReturnsValidationProblem()
    {
        // Arrange
        var (patientRef, etag) = await Client().RegisterOkAsync(NewPatient());

        // Act
        var response = await Client().UpdateAsync(patientRef, Change() with { FullName = " " }, etag);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Paciente.NomeInvalido", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Update_Unknown_ReturnsNotFound()
    {
        // Act
        var response = await Client().UpdateAsync(Guid.NewGuid(), Change(), "\"1\"");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lookup_ByCpfInAnyFormat_ReturnsPseudonym()
    {
        // Arrange
        var cpf = SyntheticPatients.Cpf();
        var (patientRef, _) = await Client().RegisterOkAsync(NewPatient(cpf));

        // Act
        var plain = await Client().LookupAsync(cpf);
        var formatted = await Client().LookupAsync($"{cpf[..3]}.{cpf[3..6]}.{cpf[6..9]}-{cpf[9..]}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(patientRef, (await plain.Content.ReadFromJsonAsync<PatientRefResponse>(Json, Ct))!.PatientRef);
        Assert.Equal(patientRef, (await formatted.Content.ReadFromJsonAsync<PatientRefResponse>(Json, Ct))!.PatientRef);
        Assert.True(plain.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Lookup_UnknownCpf_ReturnsNotFound()
    {
        // Arrange
        await Client().RegisterOkAsync(NewPatient());

        // Act
        var response = await Client().LookupAsync(SyntheticPatients.Cpf());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lookup_ClinicWithoutPatients_ReturnsNotFound()
    {
        // Act
        var response = await Client().LookupAsync(SyntheticPatients.Cpf());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Lookup_InvalidCpf_ReturnsValidationProblem()
    {
        // Act
        var response = await Client().LookupAsync("abc");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Paciente.CpfInvalido", await response.ReadProblemCodeAsync());
    }

    private static UpdatePatientRequest Change() =>
        new(SyntheticPatients.FullName(), new DateOnly(1990, 1, 1), null, SyntheticPatients.Phone());
}
