namespace CareFlow.BuildingBlocks.Tenancy;

/// <summary>
/// Contexto sem clínica, para operações de manutenção (migrations, ferramentas de design).
/// Qualquer tentativa de ler a clínica falha, então nenhum dado de tenant é tocado por engano.
/// </summary>
public sealed class NoTenantContext : ITenantContext
{
    public static readonly NoTenantContext Instance = new();

    private NoTenantContext()
    {
    }

    public Guid TenantId => throw new InvalidOperationException("Esta operação não roda no contexto de uma clínica.");

    public bool TryGetTenantId(out Guid tenantId)
    {
        tenantId = Guid.Empty;
        return false;
    }
}
