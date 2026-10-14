using System.Net;

using CareFlow.PatientVault.IntegrationTests.Infrastructure;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Patients;

public sealed class AuthorizationTests(VaultApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    [Fact]
    public async Task AnonymousRequest_ReturnsUnauthorized()
    {
        // Act
        var response = await api.CreateAnonymousClient().RegisterAsync(NewPatient());

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenWithoutClinic_IsForbidden()
    {
        // Act
        var response = await api.CreateClient(tenantId: null, Read, Write).RegisterAsync(NewPatient());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyScope_CannotRegisterOrUpdate()
    {
        // Arrange
        var (patientRef, etag) = await api.CreateClient(_clinic, Write).RegisterOkAsync(NewPatient());
        var reader = api.CreateClient(_clinic, Read);

        // Act
        var register = await reader.RegisterAsync(NewPatient());
        var update = await reader.UpdateAsync(patientRef, new("Nome Sintetico", new DateOnly(1990, 1, 1), null, null), etag);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    public async Task WriteOnlyScope_CannotReadPersonalDataOrLookup()
    {
        // Arrange: escrita não implica leitura no cofre (menor privilégio).
        var request = NewPatient();
        var writer = api.CreateClient(_clinic, Write);
        var (patientRef, _) = await writer.RegisterOkAsync(request);

        // Act
        var get = await writer.GetAsync($"/v1/patients/{patientRef}", Ct);
        var lookup = await writer.LookupAsync(request.Cpf);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, lookup.StatusCode);
    }

    [Fact]
    public async Task SchedulingScope_CannotTouchVault()
    {
        // Act
        var response = await api.CreateClient(_clinic, "scheduling.read", "scheduling.write").RegisterAsync(NewPatient());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_AreAnonymousAndHealthy(string path)
    {
        // Act
        var response = await api.CreateAnonymousClient().GetAsync(path, Ct);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
