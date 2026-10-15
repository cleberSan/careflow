using System.Net;
using System.Text;

using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Contracts.Auditing;
using CareFlow.Contracts.Consents;
using CareFlow.Contracts.Patients;
using CareFlow.EndToEndTests.Infrastructure;

namespace CareFlow.EndToEndTests.Flows;

/// <summary>
/// Entrega pelo menos uma vez significa duplicatas e desordem. Aqui as mensagens são forjadas direto no
/// broker para provocar os casos que o despachante raramente produz: a mesma mensagem duas vezes, versões
/// fora de ordem e mensagens que nunca vão ser processáveis.
/// </summary>
[Collection(SequentialFlows.Name)]
public sealed class DeliveryGuaranteesTests(CareFlowSystem system)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static DateTimeOffset Slot(int day) =>
        new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(day).AddHours(11);

    /// <summary>
    /// A fila é processada em ordem por um único consumidor: quando o marcador aparece no Inbox, tudo o que
    /// foi publicado antes dele já foi tratado (aplicado ou descartado).
    /// </summary>
    private async Task DrainSchedulingAsync(Guid clinic)
    {
        var marker = Guid.CreateVersion7();
        await system.Broker.PublishAsync(clinic, new PatientRegistered(Guid.NewGuid(), Now), marker);
        await Eventually.SucceedsAsync(
            async () => await CareFlowApi.InboxCountAsync(system.Scheduling, marker) == 1,
            description: "marcador processado pela agenda");
    }

    [Fact]
    public async Task DuplicateMessage_IsAppliedOnce()
    {
        // Arrange
        var clinic = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var messageId = Guid.CreateVersion7();
        var grant = new ConsentGranted(patient, ConsentPurposes.Scheduling, "specific-consent", "2026-10", 1, Now);

        // Act
        await system.Broker.PublishAsync(clinic, grant, messageId);
        await system.Broker.PublishAsync(clinic, grant, messageId);
        await DrainSchedulingAsync(clinic);

        // Assert
        Assert.Equal(1, await CareFlowApi.InboxCountAsync(system.Scheduling, messageId));
    }

    [Fact]
    public async Task DuplicateAccessRecord_EntersTheChainOnce()
    {
        // Arrange
        var api = new CareFlowApi(system, Guid.NewGuid());
        var resource = Guid.NewGuid();
        var messageId = Guid.CreateVersion7();
        var access = new DataAccessRecorded("scheduling", "usuario-sintetico", "appointment.read", "patient", resource, Now);
        var marker = new DataAccessRecorded("scheduling", "usuario-sintetico", "appointment.booked", "patient", resource, Now);

        // Act
        await system.Broker.PublishAsync(api.Clinic, access, messageId);
        await system.Broker.PublishAsync(api.Clinic, access, messageId);
        await system.Broker.PublishAsync(api.Clinic, marker, Guid.CreateVersion7());

        // Assert
        var trail = await Eventually.GetAsync(
            () => api.AuditTrailAsync(resource),
            entries => entries.Any(e => e.Action == "appointment.booked"),
            description: "marcador gravado na cadeia");
        Assert.Single(trail, e => e.Action == "appointment.read");
        Assert.True((await api.VerifyChainAsync()).Intact);
    }

    [Fact]
    public async Task OutOfOrderConsentVersions_KeepTheNewestDecision()
    {
        // Arrange: o paciente existe na agenda; a revogação (v2) chega antes da concessão que ela substituiu (v1).
        var api = new CareFlowApi(system, Guid.NewGuid());
        var patient = Guid.NewGuid();
        await system.Broker.PublishAsync(api.Clinic, new PatientRegistered(patient, Now), Guid.CreateVersion7());

        // Act
        await system.Broker.PublishAsync(api.Clinic, new ConsentRevoked(patient, ConsentPurposes.Scheduling, 2, Now), Guid.CreateVersion7());
        await system.Broker.PublishAsync(
            api.Clinic, new ConsentGranted(patient, ConsentPurposes.Scheduling, "specific-consent", "2026-10", 1, Now), Guid.CreateVersion7());
        await DrainSchedulingAsync(api.Clinic);

        // Assert
        var attempt = await api.BookAsync(patient, Slot(60));
        Assert.Equal(HttpStatusCode.Conflict, attempt.Status);
        Assert.Equal("Agenda.PacienteSemBaseLegal", attempt.Code);
    }

    [Theory]
    [InlineData("corpo-invalido", true)]
    [InlineData("sem-clinica", false)]
    public async Task UnprocessableMessage_GoesToDeadLetterQueue(string scenario, bool withTenant)
    {
        // Arrange
        var clinic = Guid.NewGuid();
        var messageId = Guid.CreateVersion7();
        var body = withTenant
            ? Encoding.UTF8.GetBytes("{ isto não é json")
            : Encoding.UTF8.GetBytes("""{"patientRef":"00000000-0000-0000-0000-000000000001","purpose":"scheduling","version":1,"revokedAt":"2026-10-15T00:00:00Z"}""");

        // Act
        await system.Broker.PublishRawAsync(ConsentRevoked.EventType, withTenant ? clinic.ToString() : null, body, messageId);

        // Assert: vai para mensagens mortas em vez de voltar para a fila para sempre (e travar as seguintes).
        var deadLetterQueue = MessagingTopology.DeadLetterQueueFor("scheduling");
        await Eventually.SucceedsAsync(
            async () =>
            {
                var message = await system.Broker.TakeAsync(deadLetterQueue);
                return message?.BasicProperties.MessageId == messageId.ToString();
            },
            description: $"mensagem '{scenario}' na fila de mensagens mortas");
        Assert.Equal(0, await CareFlowApi.InboxCountAsync(system.Scheduling, messageId));
        await DrainSchedulingAsync(clinic);
    }

    [Fact]
    public async Task UnroutedEvent_IsKeptInsteadOfDropped()
    {
        // Arrange
        var messageId = Guid.CreateVersion7();

        // Act: nenhum serviço consome este tipo.
        await system.Broker.PublishRawAsync("evento.sem-consumidor", Guid.NewGuid().ToString(), "{}"u8.ToArray(), messageId);

        // Assert
        await Eventually.SucceedsAsync(
            async () => (await system.Broker.TakeAsync(MessagingTopology.UnroutedQueue))?.BasicProperties.MessageId == messageId.ToString(),
            description: "evento guardado na fila de não roteados");
    }
}
