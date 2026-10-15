using CareFlow.Audit.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using Npgsql;

using static CareFlow.Audit.IntegrationTests.Infrastructure.AuditClient;

namespace CareFlow.Audit.IntegrationTests.Chain;

/// <summary>
/// Adulteração direta no banco, com o papel de admin e os gatilhos desligados só na sessão do teste
/// (<c>session_replication_role = replica</c>): é o pior caso, alguém com acesso total ao banco mas sem a chave.
/// </summary>
public sealed class TamperingTests(AuditApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    [Fact]
    public async Task AlteredField_IsDetectedAtThatEntry()
    {
        // Arrange
        await AppendManyAsync(4);

        // Act
        await TamperAsync("UPDATE audit_entries SET action = 'patient.exported' WHERE tenant_id = @t AND sequence = 2");
        var result = await api.CreateClient(_clinic, Read).VerifyOkAsync();

        // Assert
        Assert.False(result.Intact);
        Assert.Equal("HashMismatch", result.Failure);
        Assert.Equal(2, result.BrokenAtSequence);
        Assert.Equal(1, result.EntriesVerified);
    }

    [Fact]
    public async Task ChangedActor_IsDetected()
    {
        // Arrange
        await AppendManyAsync(2);

        // Act: troca quem acessou.
        await TamperAsync("UPDATE audit_entries SET actor = 'outro-usuario' WHERE tenant_id = @t AND sequence = 1");
        var result = await api.CreateClient(_clinic, Read).VerifyOkAsync();

        // Assert
        Assert.Equal("HashMismatch", result.Failure);
        Assert.Equal(1, result.BrokenAtSequence);
    }

    [Fact]
    public async Task InvalidValueInjected_IsDetected()
    {
        // Arrange
        await AppendManyAsync(2);

        // Act: valor que o domínio nunca gravaria (quebra de linha).
        await TamperAsync("UPDATE audit_entries SET actor = E'linha\\nforjada' WHERE tenant_id = @t AND sequence = 2");
        var result = await api.CreateClient(_clinic, Read).VerifyOkAsync();

        // Assert
        Assert.Equal("HashMismatch", result.Failure);
        Assert.Equal(2, result.BrokenAtSequence);
    }

    [Fact]
    public async Task DeletedMiddleEntry_IsDetected()
    {
        // Arrange
        await AppendManyAsync(4);

        // Act
        await TamperAsync("DELETE FROM audit_entries WHERE tenant_id = @t AND sequence = 3");
        var result = await api.CreateClient(_clinic, Read).VerifyOkAsync();

        // Assert
        Assert.Equal("SequenceGap", result.Failure);
        Assert.Equal(3, result.BrokenAtSequence);
    }

    [Fact]
    public async Task DeletedTail_IsDetectedByHead()
    {
        // Arrange
        await AppendManyAsync(4);

        // Act
        await TamperAsync("DELETE FROM audit_entries WHERE tenant_id = @t AND sequence >= 3");
        var result = await api.CreateClient(_clinic, Read).VerifyOkAsync();

        // Assert
        Assert.Equal("HeadMismatch", result.Failure);
        Assert.Equal(3, result.BrokenAtSequence);
    }

    [Fact]
    public async Task SwappedHashes_AreDetected()
    {
        // Arrange
        await AppendManyAsync(3);

        // Act: tenta "consertar" a entrada 2 copiando o hash da 3.
        await TamperAsync(
            "UPDATE audit_entries SET hash = (SELECT hash FROM audit_entries WHERE tenant_id = @t AND sequence = 3) WHERE tenant_id = @t AND sequence = 2");
        var result = await api.CreateClient(_clinic, Read).VerifyOkAsync();

        // Assert
        Assert.False(result.Intact);
        Assert.Equal(2, result.BrokenAtSequence);
    }

    [Fact]
    public async Task UnknownKeyId_IsDetected()
    {
        // Arrange
        await AppendManyAsync(1);

        // Act
        await TamperAsync("UPDATE audit_entries SET key_id = 'chave-inexistente' WHERE tenant_id = @t");
        var result = await api.CreateClient(_clinic, Read).VerifyOkAsync();

        // Assert
        Assert.Equal("UnknownKey", result.Failure);
    }

    [Fact]
    public async Task BrokenChain_IsLoggedAsCritical()
    {
        // Arrange
        await AppendManyAsync(2);
        await TamperAsync("DELETE FROM audit_entries WHERE tenant_id = @t AND sequence = 1");

        // Act
        await api.CreateClient(_clinic, Read).VerifyOkAsync();

        // Assert
        Assert.Contains(api.Logs.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Critical
            && entry.Message.Contains("adulterada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TamperingOneClinic_DoesNotAffectAnother()
    {
        // Arrange
        var other = Guid.NewGuid();
        await AppendManyAsync(2);
        await api.AppendAsync(NewEvent(other), Ct);

        // Act
        await TamperAsync("UPDATE audit_entries SET action = 'x' WHERE tenant_id = @t AND sequence = 1");

        // Assert
        Assert.False((await api.CreateClient(_clinic, Read).VerifyOkAsync()).Intact);
        Assert.True((await api.CreateClient(other, Read).VerifyOkAsync()).Intact);
    }

    private async Task AppendManyAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await api.AppendAsync(NewEvent(_clinic), Ct);
        }
    }

    private async Task TamperAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(api.AdminConnectionString);
        await admin.OpenAsync(Ct);
        await admin.ExecuteAsync("SET session_replication_role = replica");
        await admin.ExecuteAsync(sql, ("t", _clinic));
    }
}
