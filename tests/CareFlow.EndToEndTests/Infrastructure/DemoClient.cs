extern alias demo;

using System.Net;
using System.Net.Http.Json;

using demo::CareFlow.Demo.Api.Features;
using demo::CareFlow.Demo.Api.Telemetry;

using Microsoft.AspNetCore.Mvc.Testing;

namespace CareFlow.EndToEndTests.Infrastructure;

/// <summary>
/// Um visitante do Painel: fala só com o Demo, como a rota do site faria, com o cookie de sessão e o cabeçalho
/// anti-CSRF. HTTPS, porque o cookie <c>__Host-</c> só existe com Secure.
/// </summary>
internal sealed class DemoClient : IDisposable
{
    public const string SiteOrigin = "https://site.e2e";

    private readonly HttpClient _http;

    public DemoClient(WebApplicationFactory<demo::Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost/"), HandleCookies = true });
        _http.DefaultRequestHeaders.Add("X-CareFlow-Demo", "1");
        _http.DefaultRequestHeaders.Add("Origin", SiteOrigin);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public HttpClient Http => _http;

    public SessionView Session { get; private set; } = null!;

    public async Task<HttpResponseMessage> OpenRawAsync() => await _http.PostAsync("demo/v1/sessions", null, Ct);

    public async Task<SessionView> OpenAsync()
    {
        var response = await OpenRawAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Session = (await response.Content.ReadFromJsonAsync<SessionView>(Ct))!;
        return Session;
    }

    public async Task<BookingView> BookAsync()
    {
        var response = await _http.PostAsync("demo/v1/appointments", null, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Reserva pelo Demo: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<BookingView>(Ct))!;
    }

    public Task<PatientViews> PatientAsync() => GetAsync<PatientViews>("demo/v1/patient");

    public async Task<ActionView> RevokeAsync()
    {
        var response = await _http.PostAsync("demo/v1/consents/scheduling/revoke", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ActionView>(Ct))!;
    }

    public Task<PropagationView> PropagationAsync() => GetAsync<PropagationView>("demo/v1/propagation");

    public Task<ChannelsView> ChannelsAsync() => GetAsync<ChannelsView>("demo/v1/channels");

    public async Task<ChannelsView> DropWhatsAppAsync()
    {
        var response = await _http.PutAsync("demo/v1/channels/whatsapp/fault", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelsView>(Ct))!;
    }

    public async Task<ChannelsView> RestoreWhatsAppAsync()
    {
        var response = await _http.DeleteAsync("demo/v1/channels/whatsapp/fault", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelsView>(Ct))!;
    }

    public Task<HttpResponseMessage> TraceRawAsync(string traceId) => _http.GetAsync($"demo/v1/traces/{traceId}", Ct);

    public Task<TraceView> TraceAsync(string traceId) => GetAsync<TraceView>($"demo/v1/traces/{traceId}");

    public Task<LogView[]> LogsAsync(string traceId) => GetAsync<LogView[]>($"demo/v1/traces/{traceId}/logs");

    public async Task EndAsync()
    {
        var response = await _http.DeleteAsync("demo/v1/session", Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();

    private async Task<T> GetAsync<T>(string path)
    {
        var response = await _http.GetAsync(path, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }
}
