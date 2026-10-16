using System.Diagnostics.CodeAnalysis;

using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CareFlow.Rights.Api.Infrastructure.Persistence;

/// <summary>Usada só pelo <c>dotnet ef</c> para gerar migrations; nunca conecta ao banco.</summary>
[ExcludeFromCodeCoverage]
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RightsDbContext>
{
    public RightsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RightsDbContext>()
            .UseNpgsql("Host=localhost;Database=careflow_rights")
            .Options;

        return new RightsDbContext(options, NoTenantContext.Instance);
    }
}
