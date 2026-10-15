namespace CareFlow.Contracts.Consents;

/// <summary>
/// Consentimento (ou outra base legal) concedido para uma finalidade. <see cref="Version"/> cresce a cada
/// mudança do registro: aplique só versões maiores que a conhecida, para tolerar duplicatas e desordem.
/// </summary>
public sealed record ConsentGranted(
    Guid PatientRef,
    string Purpose,
    string LegalBasis,
    string TermVersion,
    long Version,
    DateTimeOffset GrantedAt) : IIntegrationEvent
{
    public static string EventType => "consent.granted";
}
