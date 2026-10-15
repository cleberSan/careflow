using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using CareFlow.Testing;

using Npgsql;

namespace CareFlow.EndToEndTests.Infrastructure;

/// <summary>Chamadas HTTP aos quatro serviços, como um cliente da clínica faria. Só dados sintéticos.</summary>
public sealed class CareFlowApi(CareFlowSystem system, Guid clinic)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Guid Clinic { get; } = clinic;

    public SyntheticPatient LastPatient { get; private set; } = null!;

    /// <summary>Cadastra um paciente sintético no cofre e devolve o pseudônimo.</summary>
    public async Task<Guid> RegisterPatientAsync()
    {
        LastPatient = new SyntheticPatient(SyntheticPatients.FullName(), SyntheticPatients.Cpf(), SyntheticPatients.Email(), SyntheticPatients.Phone());
        var response = await system.Vault.Client(Clinic, "vault.write").PostAsJsonAsync(
            "/v1/patients",
            new { LastPatient.FullName, LastPatient.Cpf, BirthDate = new DateOnly(1990, 5, 17), LastPatient.Email, LastPatient.Phone },
            Json,
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("patientRef").GetGuid();
    }

    public async Task GrantAsync(Guid patientRef, string purpose = "scheduling")
    {
        var response = await system.Consent.Client(Clinic, "consent.write").PutAsJsonAsync(
            $"/v1/patients/{patientRef}/consents/{purpose}",
            new { LegalBasis = "specific-consent", TermVersion = "2026-10" },
            Json,
            Ct);
        Assert.True(response.IsSuccessStatusCode, $"Concessão falhou: {response.StatusCode}");
    }

    public async Task RevokeAsync(Guid patientRef, string purpose = "scheduling")
    {
        var response = await system.Consent.Client(Clinic, "consent.write")
            .PostAsync($"/v1/patients/{patientRef}/consents/{purpose}/revoke", null, Ct);
        Assert.True(response.IsSuccessStatusCode, $"Revogação falhou: {response.StatusCode}");
    }

    public async Task<BookingAttempt> BookAsync(Guid patientRef, DateTimeOffset startsAt, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/appointments")
        {
            Content = JsonContent.Create(
                new { ProfessionalId = Guid.NewGuid(), PatientRef = patientRef, StartsAt = startsAt, EndsAt = startsAt.AddMinutes(30) },
                options: Json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        var response = await system.Scheduling.Client(Clinic, "scheduling.write").SendAsync(request, Ct);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return new BookingAttempt(
            response.StatusCode,
            body.RootElement.TryGetProperty("id", out var id) ? id.GetGuid() : null,
            body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    /// <summary>
    /// Reserva esperando a agenda conhecer o paciente (consistência eventual). Repete com a mesma
    /// Idempotency-Key, como o cliente faria ao receber 409.
    /// </summary>
    public async Task<Guid> BookWhenEligibleAsync(Guid patientRef, DateTimeOffset startsAt)
    {
        var key = Guid.NewGuid().ToString("N");
        var attempt = await Eventually.GetAsync(
            () => BookAsync(patientRef, startsAt, key),
            result => result.Status == HttpStatusCode.Created,
            description: "reserva aceita depois da propagação do cadastro e do consentimento");
        return attempt.AppointmentId!.Value;
    }

    public async Task<AppointmentView> AppointmentAsync(Guid id)
    {
        var response = await system.Scheduling.Client(Clinic, "scheduling.read").GetAsync($"/v1/appointments/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AppointmentView>(Json, Ct))!;
    }

    public async Task<AuditEntryView[]> AuditTrailAsync(Guid resourceId)
    {
        var response = await system.Audit.Client(Clinic, "audit.read").GetAsync($"/v1/audit/entries?resourceId={resourceId}&limit=200", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuditPageView>(Json, Ct))!.Items;
    }

    public async Task<ChainVerificationView> VerifyChainAsync()
    {
        var response = await system.Audit.Client(Clinic, "audit.read").GetAsync("/v1/audit/verification", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChainVerificationView>(Json, Ct))!;
    }

    /// <summary>Quantas vezes o consumidor do serviço registrou estas mensagens no Inbox (lido como admin).</summary>
    public static async Task<long> InboxCountAsync(ServiceHost service, params Guid[] messageIds)
    {
        await using var connection = new NpgsqlConnection(service.AdminConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM inbox_messages WHERE message_id = ANY($1)", connection)
        {
            Parameters = { new() { Value = messageIds } },
        };
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}

public sealed record SyntheticPatient(string FullName, string Cpf, string Email, string Phone);

public sealed record BookingAttempt(HttpStatusCode Status, Guid? AppointmentId, string? Code);

public sealed record AppointmentView(Guid Id, Guid PatientRef, string Status, string? CancellationReason);

public sealed record AuditEntryView(long Sequence, string SourceService, string Actor, string Action, string ResourceType, Guid ResourceId);

public sealed record AuditPageView(AuditEntryView[] Items, long? NextAfter);

public sealed record ChainVerificationView(bool Intact, long EntriesVerified, long LastSequence, string? Failure, long? BrokenAtSequence);
