using System.Diagnostics.CodeAnalysis;

using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CareFlow.Consent.Api.Infrastructure.Persistence;

/// <summary>Usada só pelo <c>dotnet ef</c> para gerar migrations; nunca conecta ao banco.</summary>
[ExcludeFromCodeCoverage]
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ConsentDbContext>
{
    public ConsentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ConsentDbContext>()
            .UseNpgsql("Host=localhost;Database=careflow_consent")
            .Options;

        return new ConsentDbContext(options, NoTenantContext.Instance);
    }
}
