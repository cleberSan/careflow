using System.Net;
using System.Net.Http.Json;

using CareFlow.Notifications.IntegrationTests.Infrastructure;

using static CareFlow.Notifications.IntegrationTests.Infrastructure.NotificationsClient;

namespace CareFlow.Notifications.IntegrationTests.Notifications;

public sealed class AuthorizationTests(NotificationsApiFixture api)
{
    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", $"/v1/appointments/{Guid.NewGuid()}/notifications" },
        { "GET", "/v1/settings" },
        { "PUT", "/v1/settings" },
    };

    private static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path)
        {
            Content = method == "PUT" ? JsonContent.Create(new { displayName = "Clinica", timeZone = "America/Sao_Paulo", channelOrder = Channels("Sms"), reminderLeadMinutes = 60 }) : null,
        };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Anonymous_IsUnauthorized(string method, string path)
    {
        var response = await api.CreateClient(null).SendAsync(Request(method, path), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithoutClinic_IsForbidden(string method, string path)
    {
        var response = await api.CreateClient(null, Read, Admin).SendAsync(Request(method, path), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/v1/settings")]
    [InlineData("PUT", "/v1/settings")]
    public async Task ReadScope_CannotTouchSettings(string method, string path)
    {
        var response = await api.CreateClient(Guid.NewGuid(), Read).SendAsync(Request(method, path), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OtherServiceScope_IsForbidden()
    {
        var response = await api.CreateClient(Guid.NewGuid(), "scheduling.read").GetAsync($"/v1/appointments/{Guid.NewGuid()}/notifications", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminScope_CanReadNotifications()
    {
        var response = await api.CreateClient(Guid.NewGuid(), Admin).GetAsync($"/v1/appointments/{Guid.NewGuid()}/notifications", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
