namespace CareFlow.Rights.Domain.Requests;

/// <summary>
/// Recebido (aguardando a confirmação de identidade), em andamento (os serviços estão respondendo), concluído ou
/// negado com motivo. Concluído e negado são finais.
/// </summary>
public enum SubjectRequestStatus
{
    Received = 1,
    InProgress = 2,
    Completed = 3,
    Denied = 4,
}
