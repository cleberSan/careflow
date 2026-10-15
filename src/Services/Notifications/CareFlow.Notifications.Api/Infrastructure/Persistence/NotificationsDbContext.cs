using CareFlow.BuildingBlocks.Messaging.Persistence;
using CareFlow.BuildingBlocks.Tenancy;
using CareFlow.Notifications.Domain.Appointments;
using CareFlow.Notifications.Domain.Clinics;
using CareFlow.Notifications.Domain.Consents;
using CareFlow.Notifications.Domain.Notifications;

using Microsoft.EntityFrameworkCore;

namespace CareFlow.Notifications.Api.Infrastructure.Persistence;

internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    public DbSet<NotificationJob> Jobs => Set<NotificationJob>();

    public DbSet<AppointmentTrack> Appointments => Set<AppointmentTrack>();

    public DbSet<NotificationConsent> Consents => Set<NotificationConsent>();

    public DbSet<ClinicSettings> ClinicSettings => Set<ClinicSettings>();

    public DbSet<ClinicChannelFault> ChannelFaults => Set<ClinicChannelFault>();

    public DbSet<ClinicPartition> ClinicPartitions => Set<ClinicPartition>();

    // Avaliado a cada consulta. O RLS no banco repete a mesma regra: duas camadas independentes.
    private Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);
        modelBuilder.Entity<Notification>().HasQueryFilter(n => n.TenantId == CurrentTenantId);
        modelBuilder.Entity<DeliveryAttempt>().HasQueryFilter(a => a.TenantId == CurrentTenantId);
        modelBuilder.Entity<NotificationJob>().HasQueryFilter(j => j.TenantId == CurrentTenantId);
        modelBuilder.Entity<AppointmentTrack>().HasQueryFilter(a => a.TenantId == CurrentTenantId);
        modelBuilder.Entity<NotificationConsent>().HasQueryFilter(c => c.TenantId == CurrentTenantId);
        modelBuilder.Entity<ClinicSettings>().HasQueryFilter(s => s.TenantId == CurrentTenantId);
        modelBuilder.Entity<ClinicChannelFault>().HasQueryFilter(f => f.TenantId == CurrentTenantId);
        modelBuilder.Entity<ClinicPartition>().HasQueryFilter(p => p.TenantId == CurrentTenantId);
        modelBuilder.AddOutbox(() => CurrentTenantId);
        modelBuilder.AddInbox(() => CurrentTenantId);
    }
}
