namespace CareFlow.Notifications.Api.Infrastructure.Persistence;

/// <summary>
/// Clínica sintética do Painel registrada numa partição isolada de circuit breaker, com prazo (ADR-0010). Uma
/// partição pertence a uma clínica por vez (índice único, que o banco verifica mesmo com RLS); quem distribui as
/// partições entre sessões é o serviço Demo, que só reaproveita uma depois de apagar os dados da clínica anterior.
/// </summary>
internal sealed class ClinicPartition
{
    public Guid TenantId { get; set; }

    public int Slot { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
