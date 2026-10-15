using CareFlow.Audit.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Npgsql;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Chain;

/// <summary>O que o banco garante sozinho, sem a API: append-only e isolamento por clínica (RLS).</summary>
public sealed class AppendOnlyTests(AuditApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    [Theory]
    [InlineData("UPDATE audit_entries SET action = 'x' WHERE tenant_id = @t")]
    [InlineData("DELETE FROM audit_entries WHERE tenant_id = @t")]
    public async Task ApplicationLogin_CannotChangeOrDeleteEntries(string sql)
    {
        // Arrange
        await api.AppendAsync(NewEvent(_clinic), Ct);
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinic);

        // Act & Assert: sem privilégio de UPDATE/DELETE para o papel da aplicação.
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(sql, ("t", _clinic)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Theory]
    [InlineData("UPDATE audit_entries SET action = 'x' WHERE tenant_id = @t")]
    [InlineData("DELETE FROM audit_entries WHERE tenant_id = @t")]
    public async Task EvenTheOwner_CannotChangeOrDeleteWhileTriggersAreOn(string sql)
    {
        // Arrange
        await api.AppendAsync(NewEvent(_clinic), Ct);
        await using var admin = new NpgsqlConnection(api.AdminConnectionString);
        await admin.OpenAsync(Ct);

        // Act & Assert
        var error = await Assert.ThrowsAsync<PostgresException>(() => admin.ExecuteAsync(sql, ("t", _clinic)));
        Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncate_IsRefused()
    {
        // Arrange: numa transação revertida, para nunca apagar dados dos outros testes se o gatilho falhar.
        await using var admin = new NpgsqlConnection(api.AdminConnectionString);
        await admin.OpenAsync(Ct);
        await using var transaction = await admin.BeginTransactionAsync(Ct);

        // Act & Assert
        var error = await Assert.ThrowsAsync<PostgresException>(() => admin.ExecuteAsync("TRUNCATE audit_entries"));
        Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplicationLogin_SeesOnlyItsClinic()
    {
        // Arrange
        var other = Guid.NewGuid();
        await api.AppendAsync(NewEvent(_clinic), Ct);
        await api.AppendAsync(NewEvent(other), Ct);

        // Act
        await using var mine = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinic);
        var visible = await mine.CountAsync("SELECT count(*) FROM audit_entries WHERE tenant_id = @o", ("o", other));
        await using var none = await TenantConnection.OpenAsync(api.ApplicationConnectionString, tenantId: null);
        var withoutClinic = await none.CountAsync("SELECT count(*) FROM audit_entries");

        // Assert
        Assert.Equal(0, visible);
        Assert.Equal(0, withoutClinic);
    }

    [Fact]
    public async Task ApplicationLogin_CannotInsertIntoAnotherClinic()
    {
        // Arrange
        await using var connection = await TenantConnection.OpenAsync(api.ApplicationConnectionString, _clinic);

        // Act & Assert
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            "INSERT INTO audit_chain_heads (tenant_id, sequence, hash, updated_at) VALUES (@o, 0, decode(repeat('00', 32), 'hex'), now())",
            ("o", Guid.NewGuid())));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }
}
