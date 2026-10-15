using System.Net;
using System.Net.Http.Json;

using CareFlow.Notifications.Api.Features.Settings;
using CareFlow.Notifications.IntegrationTests.Infrastructure;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

public sealed class SettingsTests(NotificationsApiFixture api)
{
    private readonly HttpClient _admin = api.CreateClient(Guid.NewGuid(), Admin);

    private Task<HttpResponseMessage> PutAsync(string? name = "Clinica Sul", string? zone = "America/Sao_Paulo", string[]? order = null, int lead = 1440) =>
        _admin.PutAsJsonAsync("/v1/settings", new { displayName = name, timeZone = zone, channelOrder = order ?? ["WhatsApp", "Email"], reminderLeadMinutes = lead }, Ct);

    [Fact]
    public async Task Put_CreatesThenUpdates()
    {
        // Act
        var created = await PutAsync();
        var updated = await PutAsync(name: "Clinica Norte", order: ["Email"], lead: 90);
        var read = await _admin.GetFromJsonAsync<ClinicSettingsResponse>("/v1/settings", Ct);

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(("Clinica Norte", 90, false), (read!.DisplayName, read.ReminderLeadMinutes, read.IsDefault));
        Assert.Equal(["Email"], read.ChannelOrder);
    }

    [Theory]
    [InlineData("whatsapp")]
    [InlineData("1")]
    [InlineData("Sms,Sms")]
    [InlineData("")]
    public async Task Put_InvalidChannelOrder_IsBadRequest(string order)
    {
        var response = await PutAsync(order: order.Split(',', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Notificacoes.OrdemDeCanaisInvalida", await response.ReadProblemCodeAsync());
    }

    [Theory]
    [InlineData("acesse www.golpe.com", null, 1440, "Notificacoes.NomeDaClinicaInvalido")]
    [InlineData("Clinica", "Lua/Base", 1440, "Notificacoes.FusoHorarioInvalido")]
    [InlineData("Clinica", "America/Sao_Paulo", 10, "Notificacoes.AntecedenciaInvalida")]
    public async Task Put_InvalidValues_AreBadRequestOnCreateAndUpdate(string name, string? zone, int lead, string code)
    {
        // Act
        var onCreate = await PutAsync(name: name, zone: zone ?? "America/Sao_Paulo", lead: lead);
        await PutAsync();
        var onUpdate = await PutAsync(name: name, zone: zone ?? "America/Sao_Paulo", lead: lead);

        // Assert
        Assert.Equal(code, await onCreate.ReadProblemCodeAsync());
        Assert.Equal(code, await onUpdate.ReadProblemCodeAsync());
        Assert.Equal("Clinica Sul", (await _admin.GetFromJsonAsync<ClinicSettingsResponse>("/v1/settings", Ct))!.DisplayName);
    }

    [Fact]
    public async Task Put_Concurrent_FirstWinsOthersConflictOrSucceed()
    {
        // Act
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PutAsync()));

        // Assert: nenhum 500; criações simultâneas viram 409 com código estável.
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
    }
}
