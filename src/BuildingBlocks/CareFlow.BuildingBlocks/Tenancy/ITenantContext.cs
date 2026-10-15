namespace CareFlow.BuildingBlocks.Tenancy;

/// <summary>Clínica (tenant) da requisição atual.</summary>
public interface ITenantContext
{
    /// <summary>Clínica atual. Lança exceção quando não há clínica válida (falha fechada).</summary>
    Guid TenantId { get; }

    /// <summary>Versão sem exceção, para quem precisa agir de forma segura também quando não há clínica.</summary>
    bool TryGetTenantId(out Guid tenantId);
}
