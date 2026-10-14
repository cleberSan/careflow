using System.Diagnostics.CodeAnalysis;

using CareFlow.BuildingBlocks.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CareFlow.PatientVault.Api.Infrastructure.Persistence;

/// <summary>Usada só pelo <c>dotnet ef</c> para gerar migrations; nunca conecta ao banco.</summary>
[ExcludeFromCodeCoverage]
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VaultDbContext>
{
    public VaultDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VaultDbContext>()
            .UseNpgsql("Host=localhost;Database=careflow_vault")
            .Options;

        return new VaultDbContext(options, NoTenantContext.Instance);
    }
}
