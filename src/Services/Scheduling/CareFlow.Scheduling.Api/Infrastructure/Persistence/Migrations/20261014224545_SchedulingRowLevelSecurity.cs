using CareFlow.BuildingBlocks.Persistence.RowLevelSecurity;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CareFlow.Scheduling.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Row-Level Security por clínica na agenda. A API passa a conectar com um papel sem privilégio de dono,
    /// que só lê e grava linhas da clínica gravada na sessão. Sem DELETE: cancelamento é uma atualização.
    /// </summary>
    public partial class SchedulingRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureApplicationRole(DatabaseExtensions.ApplicationRole);
            migrationBuilder.EnableTenantRowLevelSecurity(AppointmentConfiguration.TableName, DatabaseExtensions.ApplicationRole);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DisableTenantRowLevelSecurity(AppointmentConfiguration.TableName, DatabaseExtensions.ApplicationRole);
        }
    }
}
