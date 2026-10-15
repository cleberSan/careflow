using System.Diagnostics.CodeAnalysis;

using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CareFlow.Triage.Api.Infrastructure.Persistence;

/// <summary>Usada só pelo <c>dotnet ef</c> para gerar migrations; nunca conecta ao banco.</summary>
[ExcludeFromCodeCoverage]
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TriageDbContext>
{
    public TriageDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TriageDbContext>()
            .UseNpgsql("Host=localhost;Database=careflow_triage")
            .Options;

        return new TriageDbContext(options, NoTenantContext.Instance);
    }
}
