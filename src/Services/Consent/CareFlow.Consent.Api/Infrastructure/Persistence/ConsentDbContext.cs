using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Consent.Domain.Consents;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Consent.Api.Infrastructure.Persistence;

internal sealed class ConsentDbContext(DbContextOptions<ConsentDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<ConsentRecord> Consents => Set<ConsentRecord>();

    public DbSet<ConsentHistoryEntry> History => Set<ConsentHistoryEntry>();

    public DbSet<ErasedSubject> ErasedSubjects => Set<ErasedSubject>();

    // Avaliado a cada consulta. O RLS no banco repete a mesma regra: duas camadas independentes.
    private Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ConsentConfiguration());
        modelBuilder.ApplyConfiguration(new ConsentHistoryConfiguration());
        modelBuilder.ApplyConfiguration(new ErasedSubjectConfiguration());
        modelBuilder.Entity<ConsentRecord>().HasQueryFilter(consent => consent.TenantId == CurrentTenantId);
        modelBuilder.Entity<ConsentHistoryEntry>().HasQueryFilter(entry => entry.TenantId == CurrentTenantId);
        modelBuilder.Entity<ErasedSubject>().HasQueryFilter(subject => subject.TenantId == CurrentTenantId);
        modelBuilder.AddOutbox(() => CurrentTenantId);
        modelBuilder.AddInbox(() => CurrentTenantId);
    }
}
