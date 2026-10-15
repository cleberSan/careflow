using System.Net;
using System.Text;

using CareFlow.Demo.Api.Telemetry;

namespace CareFlow.Demo.UnitTests.Telemetry;

public sealed class GrafanaStackReaderTests
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Formato do Tempo v2: ids em Base64, kind e status como texto, atributos tipados.
    private const string TempoTrace = """
        {
          "trace": {
            "resourceSpans": [
              {
                "resource": { "attributes": [ { "key": "service.name", "value": { "stringValue": "careflow-scheduling" } } ] },
                "scopeSpans": [
                  {
                    "spans": [
                      {
                        "traceId": "S/kvNXezTaajzpKdDg5HNg==",
                        "spanId": "APBnqgupArc=",
                        "name": "POST /v1/appointments",
                        "kind": "SPAN_KIND_SERVER",
                        "startTimeUnixNano": "1760529600000000000",
                        "endTimeUnixNano": "1760529600025000000",
                        "status": { "code": "STATUS_CODE_ERROR" },
                        "attributes": [
                          { "key": "http.route", "value": { "stringValue": "/v1/appointments" } },
                          { "key": "http.response.status_code", "value": { "intValue": "201" } },
                          { "key": "url.full", "value": { "stringValue": "https://scheduling/v1/appointments?cpf=1" } }
                        ],
                        "events": [
                          { "name": "fallback", "attributes": [ { "key": "from", "value": { "stringValue": "whatsapp" } }, { "key": "phone", "value": { "stringValue": "5511" } } ] },
                          { "name": "exception", "attributes": [] }
                        ]
                      }
                    ]
                  }
                ]
              },
              {
                "resource": {},
                "instrumentationLibrarySpans": [
                  { "spans": [ { "spanId": "00f067aa0ba902b7", "parentSpanId": "00f067aa0ba902b7", "name": "legado", "kind": 5, "status": { "code": 1 } } ] }
                ]
              },
              { "resource": {} }
            ]
          }
        }
        """;

    [Fact]
    public async Task TraceAsync_ParsesTempoJson_DecodesIds_AndAppliesTheAllowList()
    {
        // Arrange
        var handler = new StubHandler(_ => Json(TempoTrace));
        var reader = new GrafanaStackReader(new StubClients(handler));

        // Act
        var trace = await reader.TraceAsync(TraceId, Ct);

        // Assert
        Assert.NotNull(trace);
        Assert.Equal($"http://tempo/api/v2/traces/{TraceId}", handler.Requests.Single().ToString());
        var server = trace.Spans[0];
        Assert.Equal("careflow-scheduling", server.Service);
        Assert.Equal("00f067aa0ba902b7", server.SpanId);
        Assert.Null(server.ParentSpanId);
        Assert.Equal("server", server.Kind);
        Assert.Equal("error", server.Status);
        Assert.Equal(25, server.DurationMs);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760529600), server.StartedAt);
        Assert.Equal("201", server.Attributes["http.response.status_code"]);
        Assert.False(server.Attributes.ContainsKey("url.full"));
        var fallback = Assert.Single(server.Events);
        Assert.Equal("fallback", fallback.Name);
        Assert.Equal(["from"], fallback.Attributes.Keys);

        var legacy = trace.Spans[1];
        Assert.Equal("?", legacy.Service);
        Assert.Equal("consumer", legacy.Kind);
        Assert.Equal("ok", legacy.Status);
        Assert.Equal("00f067aa0ba902b7", legacy.ParentSpanId);
        Assert.Equal(0, legacy.DurationMs);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"batches":[]}""")]
    public async Task TraceAsync_WithoutSpans_ReturnsNull(string body)
    {
        var reader = new GrafanaStackReader(new StubClients(new StubHandler(_ => Json(body))));

        Assert.Null(await reader.TraceAsync(TraceId, Ct));
    }

    [Fact]
    public async Task TraceAsync_NotFound_ReturnsNull()
    {
        var reader = new GrafanaStackReader(new StubClients(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        Assert.Null(await reader.TraceAsync(TraceId, Ct));
    }

    [Fact]
    public async Task TraceAsync_ServerError_Throws()
    {
        var reader = new GrafanaStackReader(new StubClients(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))));

        await Assert.ThrowsAsync<HttpRequestException>(() => reader.TraceAsync(TraceId, Ct));
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("4bf92f3577b34da6a3ce929d0e0e4736\"} | line_format \"x")]
    public async Task InvalidTraceId_NeverReachesTempoOrLoki(string traceId)
    {
        // Arrange
        var handler = new StubHandler(_ => throw new InvalidOperationException("não devia chamar"));
        var reader = new GrafanaStackReader(new StubClients(handler));

        // Act
        var trace = await reader.TraceAsync(traceId, Ct);
        var logs = await reader.LogsAsync(traceId, Ct);

        // Assert
        Assert.Null(trace);
        Assert.Empty(logs);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LogsAsync_QueriesLokiByTraceId_AndOrdersAndTruncates()
    {
        // Arrange
        var longLine = new string('x', 600);
        var body = $$"""
            {
              "data": {
                "result": [
                  {
                    "stream": { "service_name": "careflow-notifications", "severity_text": "Warning" },
                    "values": [ [ "1760529600200000000", "{{longLine}}" ] ]
                  },
                  {
                    "stream": { "service_name": "careflow-scheduling" },
                    "values": [ [ "1760529600100000000", "Consulta reservada" ] ]
                  }
                ]
              }
            }
            """;
        var handler = new StubHandler(_ => Json(body));
        var reader = new GrafanaStackReader(new StubClients(handler));

        // Act
        var logs = await reader.LogsAsync(TraceId, Ct);

        // Assert
        var query = Uri.UnescapeDataString(handler.Requests.Single().Query);
        Assert.Contains($"trace_id=\"{TraceId}\"", query, StringComparison.Ordinal);
        Assert.StartsWith("http://loki/loki/api/v1/query_range", handler.Requests.Single().ToString(), StringComparison.Ordinal);
        Assert.Equal(["careflow-scheduling", "careflow-notifications"], logs.Select(l => l.Service));
        Assert.Equal(string.Empty, logs[0].Severity);
        Assert.Equal(512, logs[1].Message.Length);
    }

    [Fact]
    public async Task LogsAsync_WithoutResult_ReturnsEmpty()
    {
        var reader = new GrafanaStackReader(new StubClients(new StubHandler(_ => Json("""{"status":"success"}"""))));

        Assert.Empty(await reader.LogsAsync(TraceId, Ct));
    }

    [Fact]
    public void ReadSpans_InvalidBase64Id_IsDropped()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            { "resourceSpans": [ { "scopeSpans": [ { "spans": [ { "spanId": "%%%", "name": "x", "kind": "SPAN_KIND_CLIENT" } ] } ] } ] }
            """);

        var span = Assert.Single(OtlpJson.ReadSpans(document.RootElement));

        Assert.Equal(string.Empty, span.SpanId);
        Assert.Equal("client", span.Kind);
        Assert.Equal("unset", span.Status);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri($"http://{name}/") };
    }
}
