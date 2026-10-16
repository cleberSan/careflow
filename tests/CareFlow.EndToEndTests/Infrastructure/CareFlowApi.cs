using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using CareFlow.Testing;

using Npgsql;

namespace CareFlow.EndToEndTests.Infrastructure;

/// <summary>Chamadas HTTP aos serviços, como um cliente da clínica faria. Só dados sintéticos.</summary>
public sealed class CareFlowApi(CareFlowSystem system, Guid clinic)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Guid Clinic { get; } = clinic;

    public SyntheticPatient LastPatient { get; private set; } = null!;

    /// <summary>Cabeçalhos extras em toda chamada (por exemplo, <c>traceparent</c> ou um <c>baggage</c> hostil).</summary>
    public Dictionary<string, string> ExtraHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lê os dados pessoais no cofre (vault.read).</summary>
    public async Task ReadPatientAsync(Guid patientRef)
    {
        var response = await Client(system.Vault, "vault.read").GetAsync($"/v1/patients/{patientRef}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Busca o pseudônimo pelo CPF (POST, para o CPF não ir na URL).</summary>
    public async Task<HttpStatusCode> LookupAsync(string cpf)
    {
        var response = await Client(system.Vault, "vault.read").PostAsJsonAsync("/v1/patients/lookup", new { Cpf = cpf }, Json, Ct);
        return response.StatusCode;
    }

    /// <summary>Tenta cadastrar de novo o último paciente (mesmo CPF): o cofre recusa.</summary>
    public async Task<HttpStatusCode> RegisterDuplicateAsync()
    {
        var response = await Client(system.Vault, "vault.write").PostAsJsonAsync(
            "/v1/patients",
            new { LastPatient.FullName, LastPatient.Cpf, BirthDate = new DateOnly(1990, 5, 17), LastPatient.Email, LastPatient.Phone },
            Json,
            Ct);
        return response.StatusCode;
    }

    private HttpClient Client(ServiceHost host, params string[] scopes)
    {
        var client = host.Client(Clinic, scopes);
        foreach (var (name, value) in ExtraHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }

        return client;
    }

    /// <summary>Cadastra um paciente sintético no cofre e devolve o pseudônimo.</summary>
    public async Task<Guid> RegisterPatientAsync()
    {
        LastPatient = new SyntheticPatient(SyntheticPatients.FullName(), SyntheticPatients.Cpf(), SyntheticPatients.Email(), SyntheticPatients.Phone());
        var response = await Client(system.Vault, "vault.write").PostAsJsonAsync(
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
        var response = await Client(system.Consent, "consent.write").PutAsJsonAsync(
            $"/v1/patients/{patientRef}/consents/{purpose}",
            new { LegalBasis = "specific-consent", TermVersion = "2026-10" },
            Json,
            Ct);
        Assert.True(response.IsSuccessStatusCode, $"Concessão falhou: {response.StatusCode}");
    }

    public async Task RevokeAsync(Guid patientRef, string purpose = "scheduling")
    {
        var response = await Client(system.Consent, "consent.write")
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
        var response = await Client(system.Scheduling, "scheduling.write").SendAsync(request, Ct);
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
        var response = await Client(system.Scheduling, "scheduling.read").GetAsync($"/v1/appointments/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AppointmentView>(Json, Ct))!;
    }

    public async Task CancelAsync(Guid appointmentId)
    {
        var response = await Client(system.Scheduling, "scheduling.write").PostAsync($"/v1/appointments/{appointmentId}/cancel", null, Ct);
        Assert.True(response.IsSuccessStatusCode, $"Cancelamento falhou: {response.StatusCode}");
    }

    /// <summary>Avisos da consulta no Notifications (vazio enquanto o evento não chegou).</summary>
    public async Task<NotificationView[]> NotificationsAsync(Guid appointmentId)
    {
        var response = await Client(system.Notifications, "notifications.read").GetAsync($"/v1/appointments/{appointmentId}/notifications", Ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NotificationView[]>(Json, Ct))!;
    }

    public async Task<AuditEntryView[]> AuditTrailAsync(Guid resourceId)
    {
        var response = await Client(system.Audit, "audit.read").GetAsync($"/v1/audit/entries?resourceId={resourceId}&limit=200", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuditPageView>(Json, Ct))!.Items;
    }

    public async Task<ChainVerificationView> VerifyChainAsync()
    {
        var response = await Client(system.Audit, "audit.read").GetAsync("/v1/audit/verification", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChainVerificationView>(Json, Ct))!;
    }

    /// <summary>
    /// Envia uma queixa à triagem depois de o consentimento ai-triage chegar ao Triage (consistência eventual),
    /// para a queixa passar pelo mascaramento e pelo modelo gravado, o caminho que mais toca dado pessoal.
    /// </summary>
    public async Task<TriageResultView> SubmitTriageAsync(Guid patientRef, string complaint)
    {
        await Eventually.GetAsync(
            async () => (await Client(system.Triage, "triage.read").GetFromJsonAsync<AiConsentResultView>($"/v1/patients/{patientRef}/ai-consent", Json, Ct))!,
            consent => consent.State == "granted",
            description: "consentimento ai-triage no Triage");
        var response = await Client(system.Triage, "triage.submit").PostAsJsonAsync("/v1/triages", new { PatientRef = patientRef, Complaint = complaint }, Json, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TriageResultView>(Json, Ct))!;
    }

    public async Task<TriageResultView> ReviewTriageAsync(Guid triageId, string decision, string? priority = null)
    {
        var response = await Client(system.Triage, "triage.review").PostAsJsonAsync($"/v1/triages/{triageId}/review", new { Decision = decision, Priority = priority }, Json, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TriageResultView>(Json, Ct))!;
    }

    /// <summary>Abre um pedido do titular (access, portability ou erasure) para o paciente.</summary>
    public async Task<SubjectRequestView> OpenSubjectRequestAsync(Guid patientRef, string kind)
    {
        var response = await Client(system.Rights, "rights.write").PostAsJsonAsync("/v1/subject-requests", new { PatientRef = patientRef, Kind = kind }, Json, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"Pedido do titular: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<SubjectRequestView>(Json, Ct))!;
    }

    public async Task<SubjectRequestView> SubjectRequestAsync(Guid requestId)
    {
        var response = await Client(system.Rights, "rights.read").GetAsync($"/v1/subject-requests/{requestId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SubjectRequestView>(Json, Ct))!;
    }

    public Task<HttpResponseMessage> SubjectRequestRawAsync(Guid requestId) =>
        Client(system.Rights, "rights.read").GetAsync($"/v1/subject-requests/{requestId}", Ct);

    /// <summary>
    /// O código de 6 dígitos como chegou ao paciente, lido no provedor simulado do Notifications (a caixa de entrada
    /// do Painel). Espera o código ser enviado e o Rights registrar o envio: a mensagem aparece na caixa antes de o
    /// evento de envio ser consumido, e confirmar nesse intervalo disputa a mesma linha do pedido (409 de concorrência).
    /// </summary>
    public async Task<string> VerificationCodeAsync(Guid requestId)
    {
        var messages = await Eventually.GetAsync(
            async () => (await Client(system.Notifications, "notifications.demo-inbox")
                .GetFromJsonAsync<VerificationMessageView[]>($"/v1/demo/verification-messages/{requestId}", Json, Ct))!,
            list => list.Length > 0,
            description: "código de confirmação na caixa de entrada simulada");
        var match = System.Text.RegularExpressions.Regex.Match(messages[^1].Text, @"\b(\d{6})\b");
        Assert.True(match.Success, "Mensagem sem código de 6 dígitos.");
        await Eventually.GetAsync(
            () => SubjectRequestAsync(requestId),
            request => request.CodeChannel is not null,
            description: "envio do código registrado no pedido");
        return match.Groups[1].Value;
    }

    public async Task<HttpResponseMessage> ConfirmIdentityRawAsync(Guid requestId, string code) =>
        await Client(system.Rights, "rights.write").PostAsJsonAsync($"/v1/subject-requests/{requestId}/identity-confirmation", new { Code = code }, Json, Ct);

    /// <summary>Confirma a identidade com o código da caixa de entrada e espera o pedido terminar.</summary>
    public async Task<SubjectRequestView> ConfirmAndWaitAsync(Guid requestId)
    {
        var code = await VerificationCodeAsync(requestId);
        var response = await ConfirmIdentityRawAsync(requestId, code);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Confirmação: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await Eventually.GetAsync(
            () => SubjectRequestAsync(requestId),
            request => request.Status is "completed" or "denied",
            description: "pedido do titular concluído por todos os serviços");
    }

    public async Task<DownloadLinkView> CreateDownloadLinkAsync(Guid requestId)
    {
        var response = await Client(system.Rights, "rights.write").PostAsync($"/v1/subject-requests/{requestId}/download-links", null, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DownloadLinkView>(Json, Ct))!;
    }

    /// <summary>Baixa o pacote pelo token, sem identidade nenhuma, como o titular faria pelo link.</summary>
    public async Task<HttpResponseMessage> DownloadAsync(string token)
    {
        using var anonymous = system.Rights.AnonymousClient();
        return await anonymous.PostAsJsonAsync("/v1/downloads", new { Token = token }, Json, Ct);
    }

    public async Task<HttpStatusCode> ReplayErasureAsync(Guid requestId)
    {
        var response = await Client(system.Rights, "rights.admin").PostAsync($"/v1/subject-requests/{requestId}/erasure-replay", null, Ct);
        return response.StatusCode;
    }

    /// <summary>Status da leitura do paciente no cofre: 200 legível, 410 eliminado, 404 não existe nesta clínica.</summary>
    public async Task<HttpStatusCode> PatientReadStatusAsync(Guid patientRef)
    {
        var response = await Client(system.Vault, "vault.read").GetAsync($"/v1/patients/{patientRef}", Ct);
        return response.StatusCode;
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

public sealed record NotificationView(Guid Id, string Kind, string Status, int Rounds, string? SentVia, string? NotSentReason, DeliveryAttemptView[] Attempts);

public sealed record DeliveryAttemptView(int Round, string Channel, string Outcome, string? Error);

public sealed record AuditEntryView(long Sequence, string SourceService, string Actor, string Action, string ResourceType, Guid ResourceId);

public sealed record AuditPageView(AuditEntryView[] Items, long? NextAfter);

public sealed record ChainVerificationView(bool Intact, long EntriesVerified, long LastSequence, string? Failure, long? BrokenAtSequence);

public sealed record AiConsentResultView(string State, long Version);

public sealed record TriageResultView(Guid Id, string Complaint, string? SentToModel, string? PromptDigest, string ModelStatus, string? SuggestedPriority, string Source, string Decision);

public sealed record SubjectRequestServiceView(string Service, string? Outcome, DateTimeOffset? AnsweredAt);

public sealed record ReceiptLineView(string Service, string Item, string Description, string Action, int Records, DateTimeOffset? RetainedUntil, string Basis);

public sealed record ErasureReceiptView(string PolicyVersion, DateTimeOffset ErasedAt, ReceiptLineView[] Lines);

public sealed record PackageInfoView(bool Available, long Size, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DestroyedAt);

public sealed record SubjectRequestView(
    Guid Id,
    Guid PatientRef,
    string Kind,
    string Status,
    string? DenialReason,
    DateTimeOffset DueAt,
    string? CodeChannel,
    DateTimeOffset? IdentityConfirmedAt,
    SubjectRequestServiceView[] Services,
    ErasureReceiptView? Receipt,
    PackageInfoView? Package);

public sealed record VerificationMessageView(string Channel, string Text, DateTimeOffset AcceptedAt);

public sealed record DownloadLinkView(string Token, DateTimeOffset ExpiresAt);
