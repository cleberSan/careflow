using System.Buffers.Text;
using System.Net;

using CareFlow.Rights.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.Rights.IntegrationTests.Infrastructure.RightsClient;

namespace CareFlow.Rights.IntegrationTests.Requests;

/// <summary>Isolamento entre clínicas, autorização e ausência de dado pessoal nos logs.</summary>
public sealed class IsolationAndSecurityTests(RightsApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();

    [Fact]
    public async Task AnotherClinic_CannotSeeConfirmOrDownload()
    {
        // Arrange
        var scenario = new RequestScenario(api, _clinic);
        var request = await scenario.OpenAndConfirmAsync("access");
        await scenario.AnswerExportAsync(await scenario.OpenedAsync(request.Id));
        var link = await scenario.Client.CreateLinkOkAsync(request.Id);
        var intruder = api.CreateClient(_other, Read, Write, Admin);

        // Act
        using var get = await intruder.GetAsync(request.Id);
        using var confirm = await intruder.ConfirmAsync(request.Id, "123456");
        using var newLink = await intruder.CreateLinkAsync(request.Id);

        // O token com a clínica trocada não acha nada: o RLS da outra clínica não enxerga o link.
        var raw = Base64Url.DecodeFromChars(link.Token);
        _other.TryWriteBytes(raw.AsSpan(0, 16));
        using var swapped = await api.CreateAnonymousClient().DownloadAsync(Base64Url.EncodeToString(raw));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, newLink.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, swapped.StatusCode);
    }

    [Fact]
    public async Task Endpoints_RequireTheRightScopes()
    {
        var request = await api.CreateClient(_clinic, Write).OpenOkAsync(Guid.NewGuid(), "access");

        using var anonymous = await api.CreateAnonymousClient().GetAsync(request.Id);
        using var readOpens = await api.CreateClient(_clinic, Read).OpenAsync(Guid.NewGuid(), "access");
        using var readConfirms = await api.CreateClient(_clinic, Read).ConfirmAsync(request.Id, "123456");
        using var noTenant = await api.CreateClient(null, Read, Write).GetAsync(request.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readOpens.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, readConfirms.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noTenant.StatusCode);
    }

    [Fact]
    public async Task AllFlows_LeaveNoPseudonymClinicCodeOrTokenInLogs()
    {
        // Arrange
        var scenario = new RequestScenario(api, _clinic);
        var client = scenario.Client;

        // Act
        var request = await client.OpenOkAsync(scenario.PatientRef, "access");
        var code = await api.CodeForAsync(_clinic, request.Id);
        using (await client.ConfirmAsync(request.Id, code == "999999" ? "999998" : "999999"))
        {
        }

        using (await client.ConfirmAsync(request.Id, code))
        {
        }

        await scenario.AnswerExportAsync(await scenario.OpenedAsync(request.Id));
        var link = await client.CreateLinkOkAsync(request.Id);
        using (await api.CreateAnonymousClient().DownloadAsync(link.Token))
        {
        }

        var erasure = await scenario.OpenAndConfirmAsync("erasure");
        await scenario.AnswerErasureAsync(erasure.Id);

        // Assert
        var own = api.Logs.Entries
            .Where(entry => entry.Category.StartsWith("CareFlow.", StringComparison.Ordinal)
                && !entry.Category.StartsWith("CareFlow.Testing.", StringComparison.Ordinal))
            .Select(entry => entry.OwnContent)
            .ToArray();
        Assert.NotEmpty(own);
        var secrets = new[]
        {
            scenario.PatientRef.ToString(), scenario.PatientRef.ToString("N"), _clinic.ToString(), _clinic.ToString("N"),
            request.Id.ToString(), code, link.Token, scenario.FullName, scenario.Cpf, scenario.Email,
        };
        foreach (var value in secrets)
        {
            Assert.DoesNotContain(own, entry => entry.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }
}
