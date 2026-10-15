using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Consent.Domain.Consents;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Consent.Api.Infrastructure.Persistence;

internal sealed class ConsentDbContext(DbContextOptions<ConsentDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<ConsentRecord> Consents => Set<ConsentRecord>();

    // Avaliado a cada consulta. O RLS no banco repete a mesma regra: duas camadas independentes.
    private Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ConsentConfiguration());
        modelBuilder.Entity<ConsentRecord>().HasQueryFilter(consent => consent.TenantId == CurrentTenantId);
        modelBuilder.AddOutbox(() => CurrentTenantId);
    }
}
