using System.Diagnostics.Metrics;

namespace CareFlow.PatientVault.Api.Common.Observability;

/// <summary>Métricas do cofre. Nenhuma tag carrega dado pessoal nem pseudônimo.</summary>
internal sealed class VaultMetrics
{
    public const string MeterName = "CareFlow.PatientVault";

    private readonly Counter<long> _registered;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _disclosures;
    private readonly Counter<long> _updated;
    private readonly Counter<long> _tenantKeysCreated;
    private readonly Counter<long> _keyUnwraps;
    private readonly Counter<long> _tenantKeysRotated;
    private readonly Counter<long> _tenantKeysDestroyed;
    private readonly Counter<long> _patientKeysDestroyed;
    private readonly Counter<long> _exported;

    public VaultMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var meter = meterFactory.Create(MeterName);
        _registered = meter.CreateCounter<long>("careflow.vault.patients.registered", "{patient}", "Pacientes cadastrados.");
        _duplicates = meter.CreateCounter<long>("careflow.vault.patients.duplicates", "{request}", "Cadastros recusados por CPF já existente na clínica.");
        _disclosures = meter.CreateCounter<long>("careflow.vault.patients.disclosed", "{patient}", "Leituras que devolveram dados pessoais decifrados.");
        _updated = meter.CreateCounter<long>("careflow.vault.patients.updated", "{patient}", "Pacientes alterados.");
        _tenantKeysCreated = meter.CreateCounter<long>("careflow.vault.tenant_keys.created", "{key}", "Chaves de dados de clínica criadas.");
        _tenantKeysRotated = meter.CreateCounter<long>("careflow.vault.tenant_keys.rotated", "{key}", "Rotações da chave de clínica.");
        _tenantKeysDestroyed = meter.CreateCounter<long>("careflow.vault.tenant_keys.destroyed", "{key}", "Versões antigas da chave de clínica destruídas.");
        _patientKeysDestroyed = meter.CreateCounter<long>("careflow.vault.patient_keys.destroyed", "{key}", "Chaves de paciente destruídas por eliminação.");
        _exported = meter.CreateCounter<long>("careflow.vault.patients.exported", "{patient}", "Seções do cofre entregues a pedidos de acesso ou portabilidade.");
        _keyUnwraps = meter.CreateCounter<long>("careflow.vault.kms.unwraps", "{call}", "Chamadas ao KMS para decifrar uma chave de dados (falhas de cache).");
    }

    public void Registered() => _registered.Add(1);

    public void Duplicate() => _duplicates.Add(1);

    public void Disclosed() => _disclosures.Add(1);

    public void Updated() => _updated.Add(1);

    public void TenantKeyCreated() => _tenantKeysCreated.Add(1);

    public void KeyUnwrapped() => _keyUnwraps.Add(1);

    public void TenantKeyRotated() => _tenantKeysRotated.Add(1);

    public void TenantKeyDestroyed() => _tenantKeysDestroyed.Add(1);

    public void PatientKeyDestroyed() => _patientKeysDestroyed.Add(1);

    public void Exported() => _exported.Add(1);
}
