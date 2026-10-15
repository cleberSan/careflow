using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using CareFlow.Notifications.Api.Common.Observability;

using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CareFlow.Notifications.Api.Contacts;

internal sealed class VaultOptions
{
    public const string SectionName = "Vault";

    [Required]
    public Uri? BaseAddress { get; set; }
}

/// <summary>
/// Lê telefone e e-mail no cofre na hora do envio (ADR-0009): o contato não é guardado aqui, e cada leitura fica
/// na trilha de auditoria do cofre com este serviço como ator. Cofre fora do ar não é "sem contato": o aviso
/// continua pendente e tenta de novo na próxima rodada.
/// </summary>
internal sealed partial class VaultContactDirectory(
    HttpClient client,
    ServiceTokenIssuer tokens,
    NotificationsMetrics metrics,
    ILogger<VaultContactDirectory> logger) : IContactDirectory
{
    public const string ClientName = "vault-contacts";

    public async Task<ContactLookup> FindAsync(Guid tenantId, Guid patientRef, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/patients/{patientRef:D}/contact");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.TokenFor(tenantId));

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Outcome(ContactLookup.NotFound, "not-found");
            }

            if (!response.IsSuccessStatusCode)
            {
                // 401/403 indicam configuração errada (chave ou emissor), não paciente ausente: o aviso espera.
                LogVaultRefused(logger, (int)response.StatusCode, patientRef);
                return Outcome(ContactLookup.Unavailable, "refused");
            }

            var body = await response.Content.ReadFromJsonAsync<VaultContactResponse>(JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
            return Outcome(ContactLookup.Found(new PatientContact(body?.Phone, body?.Email)), "found");
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException or BrokenCircuitException or JsonException
                                          || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogVaultUnavailable(logger, exception.GetType().Name, patientRef);
            return Outcome(ContactLookup.Unavailable, "unavailable");
        }
    }

    private ContactLookup Outcome(ContactLookup lookup, string outcome)
    {
        metrics.ContactLookup(outcome);
        return lookup;
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Error, Message = "Cofre recusou a leitura de contato do paciente {PatientRef} com status {StatusCode}")]
    private static partial void LogVaultRefused(ILogger logger, int statusCode, Guid patientRef);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Cofre indisponível ({Error}) ao ler o contato do paciente {PatientRef}")]
    private static partial void LogVaultUnavailable(ILogger logger, string error, Guid patientRef);

    private sealed record VaultContactResponse(string? Email, string? Phone)
    {
        public override string ToString() => "VaultContactResponse { *** }";
    }
}
