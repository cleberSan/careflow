using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace CareFlow.BuildingBlocks.Persistence;

public static class DbUpdateExceptionExtensions
{
    public static bool IsExclusionViolation(this DbUpdateException exception, string constraintName) =>
        IsViolation(exception, PostgresErrorCodes.ExclusionViolation, constraintName);

    public static bool IsUniqueViolation(this DbUpdateException exception, string constraintName) =>
        IsViolation(exception, PostgresErrorCodes.UniqueViolation, constraintName);

    /// <summary>Violação de chave estrangeira, de qualquer restrição.</summary>
    public static bool IsForeignKeyViolation(this DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };

    private static bool IsViolation(DbUpdateException exception, string sqlState, string constraintName) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == sqlState
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
