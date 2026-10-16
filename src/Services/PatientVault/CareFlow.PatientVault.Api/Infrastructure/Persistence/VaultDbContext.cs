using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.PatientVault.Api.Infrastructure.Persistence;

internal sealed class VaultDbContext(DbContextOptions<VaultDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<PatientRecord> Patients => Set<PatientRecord>();

    public DbSet<TenantKeyRecord> TenantKeys => Set<TenantKeyRecord>();

    public DbSet<PatientKeyRecord> PatientKeys => Set<PatientKeyRecord>();

    // Avaliado a cada consulta. O RLS no banco repete a mesma regra: duas camadas independentes.
    private Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PatientRecordConfiguration());
        modelBuilder.ApplyConfiguration(new TenantKeyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new PatientKeyRecordConfiguration());
        modelBuilder.Entity<PatientRecord>().HasQueryFilter(patient => patient.TenantId == CurrentTenantId);
        modelBuilder.Entity<TenantKeyRecord>().HasQueryFilter(key => key.TenantId == CurrentTenantId);
        modelBuilder.Entity<PatientKeyRecord>().HasQueryFilter(key => key.TenantId == CurrentTenantId);
        modelBuilder.AddOutbox(() => CurrentTenantId);
        modelBuilder.AddInbox(() => CurrentTenantId);
    }
}
