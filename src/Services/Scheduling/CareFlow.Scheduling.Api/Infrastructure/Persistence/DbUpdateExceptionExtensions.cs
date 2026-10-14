using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace CareFlow.Scheduling.Api.Infrastructure.Persistence;

internal static class DbUpdateExceptionExtensions
{
    public static bool IsExclusionViolation(this DbUpdateException exception, string constraintName) =>
        IsViolation(exception, PostgresErrorCodes.ExclusionViolation, constraintName);

    public static bool IsUniqueViolation(this DbUpdateException exception, string constraintName) =>
        IsViolation(exception, PostgresErrorCodes.UniqueViolation, constraintName);

    private static bool IsViolation(DbUpdateException exception, string sqlState, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == sqlState
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
