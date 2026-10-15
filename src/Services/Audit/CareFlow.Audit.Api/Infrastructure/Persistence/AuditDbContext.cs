using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Audit.Api.Infrastructure.Persistence;

internal sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<AuditEntryRecord> Entries => Set<AuditEntryRecord>();

    public DbSet<ChainHeadRecord> ChainHeads => Set<ChainHeadRecord>();

    // Avaliado a cada consulta. O RLS no banco repete a mesma regra: duas camadas independentes.
    private Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
        modelBuilder.ApplyConfiguration(new ChainHeadConfiguration());
        modelBuilder.Entity<AuditEntryRecord>().HasQueryFilter(entry => entry.TenantId == CurrentTenantId);
        modelBuilder.Entity<ChainHeadRecord>().HasQueryFilter(head => head.TenantId == CurrentTenantId);
    }
}
