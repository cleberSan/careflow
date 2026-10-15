using System.Net;
using System.Text;
using System.Text.Json;

using Anthropic;

using CareFlow.Triage.Api.Common.Observability;
using CareFlow.Triage.Api.Models;
using CareFlow.Triage.Domain.Engine;
using CareFlow.Triage.Domain.Patients;
using CareFlow.Triage.IntegrationTests.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using static CareFlow.Triage.IntegrationTests.Infrastructure.TriageClient;

namespace CareFlow.Triage.IntegrationTests.Models;

/// <summary>
/// Adaptadores do modelo sem rede de verdade: o cliente da Anthropic contra uma API simulada (formato do pedido e
/// leitura da resposta), a gravação com teto de chamadas e a reprodução das gravações por digest.
/// </summary>
public sealed class ModelAdapterTests(TriageApiFixture api) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("careflow-triage-cassettes-").FullName;
    private readonly ModelPrompt _prompt = TriagePrompt.Build(AgeBand.FromAge(30), "Tosse há dois dias.");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AnthropicTriageModel Claude(StubAnthropic stub) =>
        new(
            new AnthropicClient { ApiKey = "chave-de-teste", BaseUrl = "http://anthropic.test", HttpClient = new HttpClient(stub), MaxRetries = 0 },
            TriageModelOptions.DefaultModel,
            2048,
            NullLogger<AnthropicTriageModel>.Instance);

    [Fact]
    public async Task Anthropic_SendsInstructionsAsSystem_TheCaseAsData_AndTheSchema()
    {
        // Arrange
        var stub = new StubAnthropic(StubAnthropic.Message("""{"priority":"less-urgent","rationale":"Tosse sem sinal de gravidade no relato."}"""));

        // Act
        var reply = await Claude(stub).CompleteAsync(_prompt, Ct);

        // Assert
        var completed = Assert.IsType<ModelReply.Completed>(reply);
        Assert.Equal("""{"priority":"less-urgent","rationale":"Tosse sem sinal de gravidade no relato."}""", completed.Text);
        using var request = JsonDocument.Parse(Assert.Single(stub.Bodies));
        var root = request.RootElement;
        Assert.Equal("claude-haiku-5-5", root.GetProperty("model").GetString());
        Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(TriagePrompt.Instructions, root.GetProperty("system").GetString());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(_prompt.Payload, message.GetProperty("content").GetString());
        var format = root.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.False(format.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("chave-de-teste", stub.ApiKeys.Single());
    }

    [Theory]
    [InlineData("refusal", typeof(ModelReply.Refused))]
    [InlineData("max_tokens", typeof(ModelReply.Unavailable))]
    public async Task Anthropic_StopReasons_AreMapped(string stopReason, Type expected)
    {
        // Arrange
        var stub = new StubAnthropic(StubAnthropic.Message("{", stopReason));

        // Act
        var reply = await Claude(stub).CompleteAsync(_prompt, Ct);

        // Assert
        Assert.IsType(expected, reply);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Anthropic_ProviderErrors_AreUnavailable(HttpStatusCode status)
    {
        // Arrange
        var stub = new StubAnthropic(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"type":"error","error":{"type":"api_error","message":"x"}}""", Encoding.UTF8, "application/json"),
        });

        // Act
        var reply = await Claude(stub).CompleteAsync(_prompt, Ct);

        // Assert
        Assert.Equal(new ModelReply.Unavailable("erro-do-provedor"), reply);
    }

    [Fact]
    public async Task Recording_WritesCassettes_ThatReplayExactly()
    {
        // Arrange
        var live = new FixedModel(new ModelReply.Completed("""{"priority":"urgent","rationale":"Tosse persistente no relato."}"""));
        var recording = new RecordingTriageModel(live, _directory, maxCalls: 5, TimeProvider.System);
        var replay = new RecordedTriageModel(_directory, "claude-haiku-5-5");

        // Act
        var recorded = await recording.CompleteAsync(_prompt, Ct);
        var replayed = await replay.CompleteAsync(_prompt, Ct);

        // Assert
        Assert.Equal(recorded, replayed);
        var cassette = JsonSerializer.Deserialize<ModelCassette>(
            await File.ReadAllTextAsync(Path.Combine(_directory, _prompt.Digest + ".json"), Ct), ModelCassette.Json)!;
        Assert.Equal((_prompt.Digest, TriagePrompt.Version, "fixo", _prompt.Payload), (cassette.Digest, cassette.PromptVersion, cassette.Model, cassette.Payload));
    }

    [Fact]
    public async Task Recording_StopsAtTheCallBudget_AndSkipsFailures()
    {
        // Arrange
        var live = new FixedModel(new ModelReply.Unavailable("erro-do-provedor"));
        var recording = new RecordingTriageModel(live, _directory, maxCalls: 2, TimeProvider.System);

        // Act
        await recording.CompleteAsync(_prompt, Ct);
        await recording.CompleteAsync(_prompt, Ct);
        var third = await recording.CompleteAsync(_prompt, Ct);

        // Assert
        Assert.Equal(new ModelReply.Unavailable(RecordingTriageModel.BudgetExhausted), third);
        Assert.Equal(2, live.Calls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Refusals_AreRecordedAndReplayed()
    {
        // Arrange
        await new RecordingTriageModel(new FixedModel(new ModelReply.Refused()), _directory, 1, TimeProvider.System).CompleteAsync(_prompt, Ct);

        // Act
        var replayed = await new RecordedTriageModel(_directory, "x").CompleteAsync(_prompt, Ct);

        // Assert
        Assert.IsType<ModelReply.Refused>(replayed);
    }

    [Fact]
    public async Task Replay_MissingTamperedOrForeignCassettes_AreUnavailable()
    {
        // Arrange
        var replay = new RecordedTriageModel(_directory, "x");
        var other = TriagePrompt.Build(AgeBand.FromAge(30), "Coriza.");
        await File.WriteAllTextAsync(Path.Combine(_directory, other.Digest + ".json"), JsonSerializer.Serialize(
            new ModelCassette(_prompt.Digest, "triage-v1", "x", DateTimeOffset.UtcNow, "completed", "{}"), ModelCassette.Json), Ct);
        var odd = TriagePrompt.Build(AgeBand.FromAge(30), "Espirros.");
        await File.WriteAllTextAsync(Path.Combine(_directory, odd.Digest + ".json"), JsonSerializer.Serialize(
            new ModelCassette(odd.Digest, "triage-v1", "x", DateTimeOffset.UtcNow, "talvez", null), ModelCassette.Json), Ct);

        // Act
        var missing = await replay.CompleteAsync(_prompt, Ct);
        var foreign = await replay.CompleteAsync(other, Ct);
        var unknownOutcome = await replay.CompleteAsync(odd, Ct);
        var traversal = await replay.CompleteAsync(_prompt with { Digest = "../../etc/passwd" }, Ct);

        // Assert
        Assert.Equal(new ModelReply.Unavailable(RecordedTriageModel.MissingReason), missing);
        Assert.Equal(new ModelReply.Unavailable("gravacao-invalida"), foreign);
        Assert.Equal(new ModelReply.Unavailable("gravacao-invalida"), unknownOutcome);
        Assert.Equal(new ModelReply.Unavailable(RecordedTriageModel.MissingReason), traversal);
    }

    [Fact]
    public async Task Observed_PassesThrough_AndMapsUnexpectedErrors()
    {
        // Arrange
        var metrics = api.Services.GetRequiredService<TriageMetrics>();
        var ok = new ObservedTriageModel(new FixedModel(new ModelReply.Refused()), TimeSpan.FromSeconds(1), metrics, TimeProvider.System, NullLogger<ObservedTriageModel>.Instance);
        var broken = new ObservedTriageModel(new ThrowingModel(), TimeSpan.FromSeconds(1), metrics, TimeProvider.System, NullLogger<ObservedTriageModel>.Instance);

        // Act
        var passed = await ok.CompleteAsync(_prompt, Ct);
        var mapped = await broken.CompleteAsync(_prompt, Ct);

        // Assert
        Assert.IsType<ModelReply.Refused>(passed);
        Assert.Equal("fixo", ok.Name);
        Assert.Equal(new ModelReply.Unavailable("erro-do-provedor"), mapped);
    }

    private sealed class FixedModel(ModelReply reply) : ITriageModel
    {
        public int Calls { get; private set; }

        public string Name => "fixo";

        public Task<ModelReply> CompleteAsync(ModelPrompt prompt, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(reply);
        }
    }

    private sealed class ThrowingModel : ITriageModel
    {
        public string Name => "quebrado";

        public Task<ModelReply> CompleteAsync(ModelPrompt prompt, CancellationToken cancellationToken) =>
            throw new JsonException("resposta truncada");
    }

    /// <summary>API de mensagens simulada: guarda os corpos dos pedidos e responde o que o teste programar.</summary>
    private sealed class StubAnthropic(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public List<string?> ApiKeys { get; } = [];

        public static Func<HttpRequestMessage, HttpResponseMessage> Message(string text, string stopReason = "end_turn") => _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        id = "msg_teste",
                        type = "message",
                        role = "assistant",
                        model = "claude-haiku-5-5",
                        content = new[] { new { type = "text", text } },
                        stop_reason = stopReason,
                        stop_sequence = (string?)null,
                        usage = new { input_tokens = 900, output_tokens = 40 },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            ApiKeys.Add(request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null);
            return respond(request);
        }
    }
}
