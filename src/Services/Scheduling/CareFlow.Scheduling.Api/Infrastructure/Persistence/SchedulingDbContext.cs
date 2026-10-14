using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Scheduling.Domain.Appointments;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Scheduling.Api.Infrastructure.Persistence;

internal sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<Appointment> Appointments => Set<Appointment>();

    // Avaliado a cada consulta: toda leitura fica restrita à clínica do token.
    private Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("btree_gist");
        modelBuilder.ApplyConfiguration(new AppointmentConfiguration());
        modelBuilder.Entity<Appointment>().HasQueryFilter(appointment => appointment.TenantId == CurrentTenantId);
    }
}
