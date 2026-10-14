namespace CareFlow.Scheduling.Api.Common.Tenancy;

/// <summary>Clínica (tenant) da requisição atual.</summary>
public interface ITenantContext
{
    Guid TenantId { get; }
}
