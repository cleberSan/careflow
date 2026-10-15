using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Triage.Domain.Cases;
using CareFlow.Triage.Domain.Consents;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Triage.Api.Infrastructure.Persistence;

internal sealed class TriageDbContext(DbContextOptions<TriageDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<TriageCase> Cases => Set<TriageCase>();

    public DbSet<TriageConsent> Consents => Set<TriageConsent>();

    // Avaliado a cada consulta. O RLS no banco repete a mesma regra: duas camadas independentes.
    private Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TriageDbContext).Assembly);
        modelBuilder.Entity<TriageCase>().HasQueryFilter(c => c.TenantId == CurrentTenantId);
        modelBuilder.Entity<TriageConsent>().HasQueryFilter(c => c.TenantId == CurrentTenantId);
        modelBuilder.AddOutbox(() => CurrentTenantId);
        modelBuilder.AddInbox(() => CurrentTenantId);
    }
}
