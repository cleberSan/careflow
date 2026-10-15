namespace CareFlow.Audit.Domain.Chain;

/// <summary>
/// Entrada gravada na cadeia de uma clínica. <see cref="Hash"/> cobre o conteúdo do evento, a posição
/// (<see cref="Sequence"/>), o momento da gravação e o hash da entrada anterior: alterar, remover ou
/// reordenar qualquer entrada quebra a verificação dali em diante.
/// </summary>
public sealed record ChainedEntry(
    long Sequence,
    AuditEvent Event,
    DateTimeOffset RecordedAt,
    string KeyId,
    byte[] PreviousHash,
    byte[] Hash);
