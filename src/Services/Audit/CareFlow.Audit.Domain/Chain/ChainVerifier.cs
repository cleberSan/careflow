using System.Security.Cryptography;

namespace CareFlow.Audit.Domain.Chain;

public enum ChainFailure
{
    None = 0,

    /// <summary>A sequência pulou ou repetiu uma posição (entrada removida ou inserida).</summary>
    SequenceGap,

    /// <summary>A entrada não aponta para o hash da anterior (reordenação ou troca de entrada).</summary>
    BrokenLink,

    /// <summary>O hash não confere com o conteúdo (campo alterado).</summary>
    HashMismatch,

    /// <summary>A entrada foi assinada com uma chave que não está configurada.</summary>
    UnknownKey,

    /// <summary>A última entrada não é a cabeça registrada (entradas do fim removidas).</summary>
    HeadMismatch,
}

/// <summary>Resultado da verificação. Em falha, <see cref="BrokenAtSequence"/> é a primeira posição inválida.</summary>
public sealed record ChainVerification(
    bool IsIntact,
    long EntriesVerified,
    long LastSequence,
    ChainFailure Failure,
    long? BrokenAtSequence);

/// <summary>
/// Verifica a cadeia de uma clínica em streaming (memória constante): as entradas chegam em ordem de
/// sequência e cada uma é recalculada a partir da anterior. Para na primeira falha.
/// </summary>
public sealed class ChainVerifier(Func<string, ChainKey?> keys)
{
    private ChainHead _previous = ChainHead.Genesis;
    private long _verified;
    private ChainFailure _failure;
    private long? _brokenAt;

    public bool HasFailed => _failure != ChainFailure.None;

    /// <summary>Confere a próxima entrada. Devolve <c>false</c> na primeira falha; depois disso ignora o resto.</summary>
    public bool Accept(ChainedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(keys);

        if (HasFailed)
        {
            return false;
        }

        if (entry.Sequence != _previous.Sequence + 1)
        {
            return Fail(ChainFailure.SequenceGap, _previous.Sequence + 1);
        }

        if (!CryptographicOperations.FixedTimeEquals(entry.PreviousHash, _previous.Hash))
        {
            return Fail(ChainFailure.BrokenLink, entry.Sequence);
        }

        var key = keys(entry.KeyId);
        if (key is null)
        {
            return Fail(ChainFailure.UnknownKey, entry.Sequence);
        }

        var expected = AuditChain.ComputeHash(entry.Sequence, entry.Event, entry.RecordedAt, entry.PreviousHash, key);
        if (!CryptographicOperations.FixedTimeEquals(entry.Hash, expected))
        {
            return Fail(ChainFailure.HashMismatch, entry.Sequence);
        }

        _previous = new ChainHead(entry.Sequence, entry.Hash);
        _verified++;
        return true;
    }

    /// <summary>
    /// Fecha a verificação comparando a última entrada com a cabeça registrada. Sem essa comparação,
    /// apagar as últimas entradas deixaria uma cadeia menor, mas ainda válida. Sem cabeça registrada,
    /// passe <see cref="ChainHead.Genesis"/>: entradas sem cabeça também são adulteração.
    /// </summary>
    public ChainVerification Complete(ChainHead recordedHead)
    {
        ArgumentNullException.ThrowIfNull(recordedHead);

        if (!HasFailed
            && (recordedHead.Sequence != _previous.Sequence
                || !CryptographicOperations.FixedTimeEquals(recordedHead.Hash, _previous.Hash)))
        {
            Fail(ChainFailure.HeadMismatch, Math.Min(recordedHead.Sequence, _previous.Sequence) + 1);
        }

        return new ChainVerification(!HasFailed, _verified, _previous.Sequence, _failure, _brokenAt);
    }

    private bool Fail(ChainFailure failure, long sequence)
    {
        _failure = failure;
        _brokenAt = sequence;
        return false;
    }
}
