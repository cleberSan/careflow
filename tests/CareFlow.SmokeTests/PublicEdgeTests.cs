using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace CareFlow.SmokeTests;

/// <summary>
/// O que a internet vê do backend depois de um deploy (ADR-0013): só HTTPS, só as rotas do Painel, só a origem do
/// site, corpo pequeno, e uma sessão completa passando por todos os serviços quando há vaga.
/// </summary>
public sealed class PublicEdgeTests : IDisposable
{
    private static readonly string[] States = ["disponivel", "lotado", "desligado"];

    private readonly SmokeTarget _target = SmokeTargets.Require();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_AnswersTheSite_WithSecurityHeaders()
    {
        using var request = _target.FromSite(HttpMethod.Get, "demo/v1/status");
        using var response = await _target.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<StatusBody>(Ct);
        Assert.NotNull(status);
        Assert.Contains(status.State, States);

        Assert.Equal(_target.Origin, Header(response, "Access-Control-Allow-Origin"));
        Assert.StartsWith("max-age=", Header(response, "Strict-Transport-Security"), StringComparison.Ordinal);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.False(response.Headers.Contains("Server"), "o cabeçalho Server não deve sair");
        Assert.False(response.Headers.Contains("Set-Cookie"), "o backend não usa cookie");
    }

    [Fact]
    public async Task Preflight_AllowsOnlyTheSite()
    {
        using var fromSite = Preflight(_target.Origin);
        using var allowed = await _target.Client.SendAsync(fromSite, Ct);
        Assert.Equal(_target.Origin, Header(allowed, "Access-Control-Allow-Origin"));

        using var fromElsewhere = Preflight(SmokeTarget.ForeignOrigin);
        using var refused = await _target.Client.SendAsync(fromElsewhere, Ct);
        Assert.Null(Header(refused, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ForeignOrigin_AndMissingHeader_AreRefused()
    {
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "demo/v1/sessions");
        foreign.Headers.Add(SmokeTarget.DemoHeader, "1");
        foreign.Headers.Add("Origin", SmokeTarget.ForeignOrigin);
        using var foreignResponse = await _target.Client.SendAsync(foreign, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);

        using var withoutHeader = new HttpRequestMessage(HttpMethod.Get, "demo/v1/status");
        using var withoutHeaderResponse = await _target.Client.SendAsync(withoutHeader, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, withoutHeaderResponse.StatusCode);
    }

    [Theory]
    [InlineData("health/ready")]
    [InlineData("health/live")]
    [InlineData("metrics")]
    [InlineData("v1/patients")]
    [InlineData("v1/demo/clinic-data")]
    [InlineData("demo/v1/../health/ready")]
    [InlineData("")]
    public async Task InternalRoutes_DoNotExistFromOutside(string path)
    {
        using var request = _target.FromSite(HttpMethod.Get, path);
        using var response = await _target.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OversizedBody_StopsAtTheEdge()
    {
        using var request = _target.FromSite(HttpMethod.Post, "demo/v1/sessions");
        request.Content = new StringContent(new string('a', 16 * 1024), Encoding.UTF8, "application/json");
        using var response = await _target.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task PlainHttp_IsClosed()
    {
        // A VM só abre a 443: o certificado sai pelo desafio TLS-ALPN, sem a porta 80.
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("CAREFLOW_SMOKE_CHECK_PORT_80") == "true",
            "Só contra a VM: localmente a porta 80 do runner não diz nada.");
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var connected = false;
        try
        {
            await client.ConnectAsync(_target.BaseAddress.Host, 80, timeout.Token);
            connected = true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            // Esperado: recusada ou descartada pelo firewall.
        }

        Assert.False(connected, "a porta 80 respondeu");
    }

    [Fact]
    public async Task Session_OpensReadsThePatientAndEnds()
    {
        using (var statusRequest = _target.FromSite(HttpMethod.Get, "demo/v1/status"))
        using (var statusResponse = await _target.Client.SendAsync(statusRequest, Ct))
        {
            var status = await statusResponse.Content.ReadFromJsonAsync<StatusBody>(Ct);
            Assert.SkipUnless(status?.State == "disponivel", $"Painel ao vivo {status?.State}: sem sessão para testar agora.");
        }

        string token;
        using (var open = _target.FromSite(HttpMethod.Post, "demo/v1/sessions"))
        using (var opened = await _target.Client.SendAsync(open, Ct))
        {
            Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
            var session = await opened.Content.ReadFromJsonAsync<SessionBody>(Ct);
            Assert.NotNull(session?.Token);
            token = session.Token;
        }

        try
        {
            // Lê o paciente: passa pelo cofre (e pela chave no OCI Vault), consentimento, agenda e avisos.
            using var read = _target.FromSite(HttpMethod.Get, "demo/v1/patient", token);
            using var patient = await _target.Client.SendAsync(read, Ct);
            Assert.Equal(HttpStatusCode.OK, patient.StatusCode);
        }
        finally
        {
            using var end = _target.FromSite(HttpMethod.Delete, "demo/v1/session", token);
            using var ended = await _target.Client.SendAsync(end, Ct);
            Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        }

        using var after = _target.FromSite(HttpMethod.Get, "demo/v1/session", token);
        using var afterResponse = await _target.Client.SendAsync(after, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, afterResponse.StatusCode);
    }

    public void Dispose() => _target.Dispose();

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "demo/v1/sessions");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,x-careflow-demo");
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private sealed record StatusBody(bool Available, string State);

    private sealed record SessionBody(string? Token);
}
