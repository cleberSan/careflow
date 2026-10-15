namespace CareFlow.Contracts.Consents;

/// <summary>O titular revogou o consentimento de uma finalidade. Veja <see cref="ConsentGranted"/> sobre a versão.</summary>
public sealed record ConsentRevoked(
    Guid PatientRef,
    string Purpose,
    long Version,
    DateTimeOffset RevokedAt) : IIntegrationEvent
{
    public static string EventType => "consent.revoked";
}
