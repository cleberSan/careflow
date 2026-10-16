using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CareFlow.Demo.Api.Services;

/// <summary>Um serviço do CareFlow: nome do cliente HTTP e audiência dos tokens.</summary>
internal sealed record CareFlowService(string Name, string Audience)
{
    public static readonly CareFlowService Vault = new("vault", "careflow-patient-vault");
    public static readonly CareFlowService Consent = new("consent", "careflow-consent");
    public static readonly CareFlowService Scheduling = new("scheduling", "careflow-scheduling");
    public static readonly CareFlowService Notifications = new("notifications", "careflow-notifications");
    public static readonly CareFlowService Audit = new("audit", "careflow-audit");
    public static readonly CareFlowService Triage = new("triage", "careflow-triage");
    public static readonly CareFlowService Rights = new("rights", "careflow-rights");

    public static IReadOnlyList<CareFlowService> All { get; } = [Vault, Consent, Scheduling, Notifications, Audit, Triage, Rights];

    /// <summary>Os que guardam dados da clínica e têm a limpeza do demo (a auditoria é só de inserção).</summary>
    public static IReadOnlyList<CareFlowService> Purgeable { get; } = [Vault, Consent, Scheduling, Notifications, Triage, Rights];
}

/// <summary>Resposta inesperada de um serviço (fora do ar, configuração errada). Vira 502 sem detalhes.</summary>
internal sealed class UpstreamException(string service, HttpStatusCode? status, Exception? inner = null)
    : Exception($"Serviço {service} respondeu {(status is null ? "sem resposta" : ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture))}.", inner)
{
    public string Service { get; } = service;

    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Chamadas do Demo aos seis serviços, sempre em nome da clínica sintética da sessão e com o menor escopo de
/// cada operação. Só os campos que o Painel mostra são lidos das respostas.
/// </summary>
internal sealed class CareFlowServices(IHttpClientFactory clients)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<Guid> RegisterPatientAsync(Guid clinic, SyntheticPerson person, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Vault, clinic, ["vault.write"], HttpMethod.Post, "v1/patients",
            JsonContent.Create(new { person.FullName, person.Cpf, person.BirthDate, person.Email, person.Phone }, options: Json),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Vault, response);
        var body = await ReadAsync<PatientRefBody>(response, cancellationToken).ConfigureAwait(false);
        return body.PatientRef;
    }

    public async Task<VaultPatient> PatientAsync(Guid clinic, Guid patientRef, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(CareFlowService.Vault, clinic, ["vault.read"], HttpMethod.Get, $"v1/patients/{patientRef}", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Vault, response);
        return await ReadAsync<VaultPatient>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task GrantAsync(Guid clinic, Guid patientRef, string purpose, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Consent, clinic, ["consent.write"], HttpMethod.Put, $"v1/patients/{patientRef}/consents/{purpose}",
            JsonContent.Create(new { LegalBasis = "specific-consent", TermVersion = "demo-2026-10" }, options: Json),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Consent, response);
    }

    /// <summary>Revoga; já revogado conta como sucesso (o visitante clicou duas vezes).</summary>
    public async Task RevokeAsync(Guid clinic, Guid patientRef, string purpose, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Consent, clinic, ["consent.write"], HttpMethod.Post, $"v1/patients/{patientRef}/consents/{purpose}/revoke", null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            EnsureSuccess(CareFlowService.Consent, response);
        }
    }

    public async Task<IReadOnlyList<ConsentRecord>> ConsentsAsync(Guid clinic, Guid patientRef, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(CareFlowService.Consent, clinic, ["consent.read"], HttpMethod.Get, $"v1/patients/{patientRef}/consents", null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        EnsureSuccess(CareFlowService.Consent, response);
        return await ReadAsync<ConsentRecord[]>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BookingOutcome> BookAsync(Guid clinic, Guid patientRef, Guid professional, DateTimeOffset startsAt, string idempotencyKey, CancellationToken cancellationToken)
    {
        var content = JsonContent.Create(new { ProfessionalId = professional, PatientRef = patientRef, StartsAt = startsAt, EndsAt = startsAt.AddMinutes(30) }, options: Json);
        using var response = await SendAsync(
            CareFlowService.Scheduling, clinic, ["scheduling.write"], HttpMethod.Post, "v1/appointments", content, cancellationToken,
            request => request.Headers.Add("Idempotency-Key", idempotencyKey)).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            return new BookingOutcome((await ReadAsync<Appointment>(response, cancellationToken).ConfigureAwait(false)).Id, null);
        }

        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            var problem = await ReadAsync<ProblemBody>(response, cancellationToken).ConfigureAwait(false);
            return new BookingOutcome(null, problem.Code ?? "Agenda.Recusada");
        }

        throw new UpstreamException(CareFlowService.Scheduling.Name, response.StatusCode);
    }

    public async Task<Appointment> AppointmentAsync(Guid clinic, Guid id, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(CareFlowService.Scheduling, clinic, ["scheduling.read"], HttpMethod.Get, $"v1/appointments/{id}", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Scheduling, response);
        return await ReadAsync<Appointment>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NotificationRecord>> NotificationsAsync(Guid clinic, Guid appointmentId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Notifications, clinic, ["notifications.read"], HttpMethod.Get, $"v1/appointments/{appointmentId}/notifications", null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        EnsureSuccess(CareFlowService.Notifications, response);
        return await ReadAsync<NotificationRecord[]>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> AuditTrailAsync(Guid clinic, Guid resourceId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Audit, clinic, ["audit.read"], HttpMethod.Get, $"v1/audit/entries?resourceId={resourceId}&limit=200", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Audit, response);
        return (await ReadAsync<AuditPage>(response, cancellationToken).ConfigureAwait(false)).Items;
    }

    public async Task<ChainVerification> VerifyChainAsync(Guid clinic, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(CareFlowService.Audit, clinic, ["audit.read"], HttpMethod.Get, "v1/audit/verification", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Audit, response);
        return await ReadAsync<ChainVerification>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Envia a queixa para a triagem. POST não é repetido pelo pipeline de resiliência: uma repetição criaria outra
    /// triagem e poderia chamar o modelo duas vezes. Queixa recusada (400) e limite da clínica (429) voltam como
    /// recusa com o código do serviço; o resto é falha do serviço.
    /// </summary>
    public async Task<TriageOutcome> SubmitTriageAsync(Guid clinic, Guid patientRef, string? complaint, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Triage, clinic, ["triage.submit"], HttpMethod.Post, "v1/triages",
            JsonContent.Create(new { PatientRef = patientRef, Complaint = complaint }, options: Json),
            cancellationToken).ConfigureAwait(false);
        return await TriageOutcomeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Confirma ou corrige a sugestão, com o escopo de profissional (<c>triage.review</c>).</summary>
    public async Task<TriageOutcome> ReviewTriageAsync(Guid clinic, Guid triageId, string? decision, string? priority, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Triage, clinic, ["triage.review"], HttpMethod.Post, $"v1/triages/{triageId}/review",
            JsonContent.Create(new { Decision = decision, Priority = priority }, options: Json),
            cancellationToken).ConfigureAwait(false);
        return await TriageOutcomeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Como o Triage vê o consentimento <c>ai-triage</c> agora: granted, revoked ou unknown.</summary>
    public async Task<AiConsent> AiConsentAsync(Guid clinic, Guid patientRef, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Triage, clinic, ["triage.read"], HttpMethod.Get, $"v1/patients/{patientRef}/ai-consent", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Triage, response);
        return await ReadAsync<AiConsent>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TriageOutcome> TriageOutcomeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return new TriageOutcome(await ReadAsync<TriageRecord>(response, cancellationToken).ConfigureAwait(false), null, response.StatusCode);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests)
        {
            var problem = await ReadAsync<ProblemBody>(response, cancellationToken).ConfigureAwait(false);
            return new TriageOutcome(null, problem.Code ?? "Triagem.Recusada", response.StatusCode);
        }

        throw new UpstreamException(CareFlowService.Triage.Name, response.StatusCode);
    }

    /// <summary>Abre um pedido do titular (access, portability, erasure) para o paciente da sessão.</summary>
    public async Task<RightsOutcome> OpenRightsRequestAsync(Guid clinic, Guid patientRef, string kind, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Rights, clinic, ["rights.write"], HttpMethod.Post, "v1/subject-requests",
            JsonContent.Create(new { PatientRef = patientRef, Kind = kind }, options: Json), cancellationToken).ConfigureAwait(false);
        return await RightsOutcomeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RightsRequest> RightsRequestAsync(Guid clinic, Guid requestId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(CareFlowService.Rights, clinic, ["rights.read"], HttpMethod.Get, $"v1/subject-requests/{requestId}", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Rights, response);
        return await ReadAsync<RightsRequest>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RightsOutcome> ConfirmIdentityAsync(Guid clinic, Guid requestId, string? code, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Rights, clinic, ["rights.write"], HttpMethod.Post, $"v1/subject-requests/{requestId}/identity-confirmation",
            JsonContent.Create(new { Code = code }, options: Json), cancellationToken).ConfigureAwait(false);
        return await RightsOutcomeAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Caixa de entrada simulada do Notifications: a mensagem com o código, como o paciente a receberia.</summary>
    public async Task<IReadOnlyList<InboxMessage>> VerificationMessagesAsync(Guid clinic, Guid requestId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Notifications, clinic, ["notifications.demo-inbox"], HttpMethod.Get, $"v1/demo/verification-messages/{requestId}", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Notifications, response);
        return await ReadAsync<InboxMessage[]>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gera o link e baixa o pacote sem token, como o titular faria, e tenta o mesmo link de novo: a segunda vez
    /// precisa ser recusada. Devolve o ZIP (nulo se o pacote não existe mais) e se a reutilização foi recusada.
    /// </summary>
    public async Task<(byte[]? Zip, DateTimeOffset? LinkExpiresAt, bool ReuseRefused)> DownloadPackageAsync(Guid clinic, Guid requestId, CancellationToken cancellationToken)
    {
        using var linkResponse = await SendAsync(
            CareFlowService.Rights, clinic, ["rights.write"], HttpMethod.Post, $"v1/subject-requests/{requestId}/download-links", null, cancellationToken).ConfigureAwait(false);
        if (linkResponse.StatusCode == HttpStatusCode.Conflict)
        {
            return (null, null, false);
        }

        EnsureSuccess(CareFlowService.Rights, linkResponse);
        var link = await ReadAsync<DownloadLinkBody>(linkResponse, cancellationToken).ConfigureAwait(false);

        using var download = await SendAsync(
            CareFlowService.Rights, clinic, [], HttpMethod.Post, "v1/downloads", JsonContent.Create(new { link.Token }, options: Json), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Rights, download);
        var zip = await download.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        using var reuse = await SendAsync(
            CareFlowService.Rights, clinic, [], HttpMethod.Post, "v1/downloads", JsonContent.Create(new { link.Token }, options: Json), cancellationToken).ConfigureAwait(false);
        return (zip, link.ExpiresAt, reuse.StatusCode == HttpStatusCode.NotFound);
    }

    /// <summary>Se o cofre ainda consegue ler o paciente: 200 legível, 410 eliminado (chave destruída).</summary>
    public async Task<HttpStatusCode> VaultReadStatusAsync(Guid clinic, Guid patientRef, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(CareFlowService.Vault, clinic, ["vault.read"], HttpMethod.Get, $"v1/patients/{patientRef}", null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            return response.StatusCode;
        }

        throw new UpstreamException(CareFlowService.Vault.Name, response.StatusCode);
    }

    private static async Task<RightsOutcome> RightsOutcomeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return new RightsOutcome(await ReadAsync<RightsRequest>(response, cancellationToken).ConfigureAwait(false), null, response.StatusCode);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            var problem = await ReadAsync<ProblemBody>(response, cancellationToken).ConfigureAwait(false);
            return new RightsOutcome(null, problem.Code ?? "Direitos.Recusado", response.StatusCode);
        }

        throw new UpstreamException(CareFlowService.Rights.Name, response.StatusCode);
    }

    /// <summary>Registra a clínica na partição isolada; falso se a partição está com outra clínica.</summary>
    public async Task<bool> LeasePartitionAsync(Guid clinic, int slot, TimeSpan duration, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Notifications, clinic, ["notifications.admin"], HttpMethod.Put, "v1/fault-injection/partition",
            JsonContent.Create(new { Slot = slot, DurationSeconds = (int)Math.Ceiling(duration.TotalSeconds) }, options: Json),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        EnsureSuccess(CareFlowService.Notifications, response);
        return true;
    }

    public async Task SetFaultAsync(Guid clinic, string channel, string mode, TimeSpan duration, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            CareFlowService.Notifications, clinic, ["notifications.admin"], HttpMethod.Put, $"v1/fault-injection/{channel}",
            JsonContent.Create(new { Mode = mode, DurationSeconds = (int)Math.Ceiling(duration.TotalSeconds) }, options: Json),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Notifications, response);
    }

    public async Task ClearFaultAsync(Guid clinic, string channel, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(CareFlowService.Notifications, clinic, ["notifications.admin"], HttpMethod.Delete, $"v1/fault-injection/{channel}", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Notifications, response);
    }

    public async Task<ClinicChannels> ChannelsAsync(Guid clinic, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(CareFlowService.Notifications, clinic, ["notifications.admin"], HttpMethod.Get, "v1/fault-injection", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(CareFlowService.Notifications, response);
        return await ReadAsync<ClinicChannels>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgeAsync(CareFlowService service, Guid clinic, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        using var response = await SendAsync(service, clinic, ["demo.purge"], HttpMethod.Delete, "v1/demo/clinic-data", null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(service, response);
        return (await ReadAsync<PurgeBody>(response, cancellationToken).ConfigureAwait(false)).Rows;
    }

    private async Task<HttpResponseMessage> SendAsync(
        CareFlowService service,
        Guid clinic,
        string[] scopes,
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Options.Set(ServiceCall.Key, new ServiceCall(clinic, service.Audience, scopes));
        configure?.Invoke(request);

        var client = clients.CreateClient(service.Name);
        try
        {
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException
                                          || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new UpstreamException(service.Name, null, exception);
        }
    }

    private static void EnsureSuccess(CareFlowService service, HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new UpstreamException(service.Name, response.StatusCode);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("Corpo vazio.");
        }
        catch (JsonException exception)
        {
            throw new UpstreamException(response.RequestMessage?.RequestUri?.Host ?? "?", response.StatusCode, exception);
        }
    }

    private sealed record PatientRefBody(Guid PatientRef);

    private sealed record ProblemBody(string? Code);

    private sealed record PurgeBody(int Rows);

    private sealed record DownloadLinkBody(string Token, DateTimeOffset ExpiresAt);
}

internal sealed record SyntheticPerson(string FullName, string Cpf, DateOnly BirthDate, string Email, string Phone);

internal sealed record BookingOutcome(Guid? AppointmentId, string? RefusalCode);

internal sealed record VaultPatient(Guid PatientRef, string FullName, string Cpf, DateOnly BirthDate, string? Email, string? Phone);

internal sealed record ConsentRecord(string Purpose, string LegalBasis, string TermVersion, string Status, DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt);

internal sealed record Appointment(Guid Id, Guid PatientRef, DateTimeOffset StartsAt, string Status, DateTimeOffset? CancelledAt, string? CancellationReason);

internal sealed record DeliveryAttempt(int Round, string Channel, string Outcome, DateTimeOffset AttemptedAt);

internal sealed record NotificationRecord(Guid Id, string Kind, string Status, int Rounds, string? SentVia, string? NotSentReason, IReadOnlyList<DeliveryAttempt> Attempts);

internal sealed record AuditEntry(long Sequence, DateTimeOffset OccurredAt, string SourceService, string Actor, string Action, string ResourceType, Guid ResourceId, string? ContentDigest);

internal sealed record AuditPage(IReadOnlyList<AuditEntry> Items);

internal sealed record ChainVerification(bool Intact, long EntriesVerified, long LastSequence, string? Failure);

internal sealed record ChannelState(string Channel, string? Fault, DateTimeOffset? ExpiresAt, string Circuit);

internal sealed record ClinicChannels(string Partition, IReadOnlyList<ChannelState> Channels);

/// <summary>Triagem aceita (<see cref="Triage"/>) ou recusada pelo serviço (<see cref="RefusalCode"/>, com o status).</summary>
internal sealed record TriageOutcome(TriageRecord? Triage, string? RefusalCode, HttpStatusCode Status);

/// <summary>Os campos da triagem que o Painel mostra. O revisor (sujeito do token) não é lido.</summary>
internal sealed record TriageRecord(
    Guid Id,
    Guid PatientRef,
    string Complaint,
    string AgeBand,
    string? SentToModel,
    string? PromptDigest,
    string? PromptVersion,
    string? ModelName,
    string ModelStatus,
    string? ModelPriority,
    string? SuggestedPriority,
    string Source,
    string? Rationale,
    IReadOnlyList<string> Guardrails,
    IReadOnlyList<string> AlarmSigns,
    IReadOnlyDictionary<string, int> Masks,
    DateTimeOffset CreatedAt,
    string Decision,
    string? FinalPriority,
    DateTimeOffset? ReviewedAt)
{
    public override string ToString() => $"TriageRecord {{ Id = {Id}, *** }}";
}

internal sealed record AiConsent(string State, long Version);

internal sealed record RightsService(string Service, string? Outcome, DateTimeOffset? AnsweredAt);

internal sealed record RightsReceiptLine(string Service, string Item, string Description, string Action, int Records, DateTimeOffset? RetainedUntil, string Basis);

internal sealed record RightsReceipt(string PolicyVersion, DateTimeOffset ErasedAt, IReadOnlyList<RightsReceiptLine> Lines);

internal sealed record RightsPackage(bool Available, long Size, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DestroyedAt);

/// <summary>Os campos do pedido que o Painel mostra.</summary>
internal sealed record RightsRequest(
    Guid Id,
    string Kind,
    string Status,
    string? DenialReason,
    DateTimeOffset ReceivedAt,
    DateTimeOffset DueAt,
    bool Overdue,
    DateTimeOffset? CodeSentAt,
    string? CodeChannel,
    DateTimeOffset? IdentityConfirmedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<RightsService> Services,
    RightsReceipt? Receipt,
    RightsPackage? Package);

/// <summary>Pedido aceito ou recusado pelo serviço (código e status do problema).</summary>
internal sealed record RightsOutcome(RightsRequest? Request, string? RefusalCode, HttpStatusCode Status);

internal sealed record InboxMessage(string Channel, string Text, DateTimeOffset AcceptedAt);
