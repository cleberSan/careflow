using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using CareFlow.BuildingBlocks.AspNetCore.Security.ServiceTokens;
using CareFlow.BuildingBlocks.Diagnostics;
using CareFlow.Triage.Domain.Patients;
using CareFlow.Triage.Domain.Pseudonymization;

using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CareFlow.Triage.Api.Profiles;

internal sealed class VaultOptions
{
    public const string SectionName = "Vault";

    [Required]
    public Uri? BaseAddress { get; set; }
}

/// <summary>
/// Lê no cofre só idade e nome (rota <c>triage-profile</c>), com token de serviço por clínica e escopo próprio.
/// A idade vira faixa aqui; o nome serve para mascarar a queixa e é descartado com a requisição. Cada leitura
/// fica na trilha de auditoria do cofre com este serviço como ator.
/// </summary>
internal sealed partial class VaultTriageProfiles(
    HttpClient client,
    ServiceTokenIssuer tokens,
    ILogger<VaultTriageProfiles> logger) : ITriageProfiles
{
    public const string ClientName = "vault-triage-profiles";

    /// <summary>Token só para o cofre e só com o escopo do perfil de triagem.</summary>
    public static readonly ServiceTokenGrant Grant = new("careflow-triage", "careflow-patient-vault", "vault.triage-profile.read");

    public async Task<TriageProfileLookup> FindAsync(Guid tenantId, Guid patientRef, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/patients/{patientRef:D}/triage-profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.TokenFor(tenantId));

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return TriageProfileLookup.NotFound;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogVaultRefused(logger, (int)response.StatusCode, LogMask.Pseudonym(patientRef));
                return TriageProfileLookup.Unavailable;
            }

            var body = await response.Content.ReadFromJsonAsync<VaultTriageProfile>(JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
            if (body is null || body.Age is < 0 or > 150)
            {
                return TriageProfileLookup.Unavailable;
            }

            return TriageProfileLookup.Found(new TriageProfile(AgeBand.FromAge(body.Age), KnownIdentity.FromFullName(body.FullName)));
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException or BrokenCircuitException or JsonException
                                          || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogVaultUnavailable(logger, exception.GetType().Name, LogMask.Pseudonym(patientRef));
            return TriageProfileLookup.Unavailable;
        }
    }

    [LoggerMessage(EventId = 7101, Level = LogLevel.Error, Message = "Cofre recusou a leitura do perfil de triagem do paciente {PatientRef} com status {StatusCode}")]
    private static partial void LogVaultRefused(ILogger logger, int statusCode, string patientRef);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Warning, Message = "Cofre indisponível ({Error}) ao ler o perfil de triagem do paciente {PatientRef}")]
    private static partial void LogVaultUnavailable(ILogger logger, string error, string patientRef);

    private sealed record VaultTriageProfile(int Age, string? FullName)
    {
        public override string ToString() => "VaultTriageProfile { *** }";
    }
}

internal static class TriageProfileExtensions
{
    /// <summary>Cliente do cofre com o pipeline padrão de resiliência. O emissor local de tokens é recusado em produção.</summary>
    public static IServiceCollection AddTriageProfiles(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddLocalServiceTokens(environment, VaultTriageProfiles.Grant);
        services.AddOptions<VaultOptions>()
            .BindConfiguration(VaultOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<ITriageProfiles, VaultTriageProfiles>(VaultTriageProfiles.ClientName, (provider, client) =>
            {
                client.BaseAddress = provider.GetRequiredService<IOptions<VaultOptions>>().Value.BaseAddress;
            })
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(8);
                options.Retry.MaxRetryAttempts = 2;
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            });

        return services;
    }
}
