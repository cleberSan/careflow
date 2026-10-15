namespace CareFlow.Contracts.Patients;

/// <summary>O cofre cadastrou um paciente. Só o pseudônimo sai do cofre.</summary>
public sealed record PatientRegistered(Guid PatientRef, DateTimeOffset RegisteredAt) : IIntegrationEvent
{
    public static string EventType => "patient.registered";
}
