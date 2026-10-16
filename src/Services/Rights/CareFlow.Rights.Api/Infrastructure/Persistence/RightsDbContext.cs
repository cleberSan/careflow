using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Rights.Domain.Requests;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Rights.Api.Infrastructure.Persistence;

internal sealed class RightsDbContext(DbContextOptions<RightsDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<SubjectRequest> Requests => Set<SubjectRequest>();

    public DbSet<SubjectRequestAnswer> Answers => Set<SubjectRequestAnswer>();

    public DbSet<SubjectRequestPackage> Packages => Set<SubjectRequestPackage>();

    public DbSet<DownloadLink> DownloadLinks => Set<DownloadLink>();

    /// <summary>Sem filtro por clínica, de propósito: ver <see cref="PackageExpiration"/>.</summary>
    public DbSet<PackageExpiration> PackageExpirations => Set<PackageExpiration>();

    // Avaliado a cada consulta. O RLS no banco repete a mesma regra: duas camadas independentes.
    private Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SubjectRequestConfiguration());
        modelBuilder.ApplyConfiguration(new SubjectRequestAnswerConfiguration());
        modelBuilder.ApplyConfiguration(new SubjectRequestPackageConfiguration());
        modelBuilder.ApplyConfiguration(new DownloadLinkConfiguration());
        modelBuilder.ApplyConfiguration(new PackageExpirationConfiguration());
        modelBuilder.Entity<SubjectRequest>().HasQueryFilter(r => r.TenantId == CurrentTenantId);
        modelBuilder.Entity<SubjectRequestAnswer>().HasQueryFilter(a => a.TenantId == CurrentTenantId);
        modelBuilder.Entity<SubjectRequestPackage>().HasQueryFilter(p => p.TenantId == CurrentTenantId);
        modelBuilder.Entity<DownloadLink>().HasQueryFilter(l => l.TenantId == CurrentTenantId);
        modelBuilder.AddOutbox(() => CurrentTenantId);
        modelBuilder.AddInbox(() => CurrentTenantId);
    }
}
