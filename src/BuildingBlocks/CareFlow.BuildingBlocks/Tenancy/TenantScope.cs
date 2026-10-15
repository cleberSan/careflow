namespace CareFlow.BuildingBlocks.Tenancy;

/// <summary>
/// Clínica de um escopo de DI que não vem de uma requisição HTTP, como o processamento de uma mensagem.
/// Entra uma vez por escopo; tentar trocar de clínica no mesmo escopo é erro de programação e lança exceção,
/// para que uma conexão nunca carregue a clínica de outra mensagem.
/// </summary>
public sealed class TenantScope
{
    private Guid _tenantId;

    public bool HasTenant => _tenantId != Guid.Empty;

    public void Enter(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A clínica do escopo não pode ser vazia.", nameof(tenantId));
        }

        if (HasTenant && _tenantId != tenantId)
        {
            throw new InvalidOperationException("Este escopo já pertence a outra clínica.");
        }

        _tenantId = tenantId;
    }

    public bool TryGetTenantId(out Guid tenantId)
    {
        tenantId = _tenantId;
        return HasTenant;
    }
}
