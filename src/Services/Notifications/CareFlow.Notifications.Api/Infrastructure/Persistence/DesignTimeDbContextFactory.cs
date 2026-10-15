using System.Diagnostics.CodeAnalysis;

using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CareFlow.Notifications.Api.Infrastructure.Persistence;

/// <summary>Usada só pelo <c>dotnet ef</c> para gerar migrations; nunca conecta ao banco.</summary>
[ExcludeFromCodeCoverage]
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql("Host=localhost;Database=careflow_notifications")
            .Options;

        return new NotificationsDbContext(options, NoTenantContext.Instance);
    }
}
