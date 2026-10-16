using System.Text.Json;

namespace CareFlow.Demo.Api.Sessions;

/// <summary>Uma clínica sintética criada pelo Demo e ainda não apagada nos serviços.</summary>
internal sealed record LedgerEntry(Guid Clinic, int Slot, DateTimeOffset CreatedAt);

/// <summary>
/// Registro das clínicas que o Demo criou e ainda não apagou. A entrada nasce antes de qualquer dado existir nos
/// serviços e só sai depois da limpeza confirmada, então um reinício (deploy, queda, falta de memória) não deixa
/// clínica de visitante para trás: na subida, a limpeza começa pelo que estiver aqui (ADR-0013).
/// </summary>
internal interface IClinicLedger
{
    void Add(LedgerEntry entry);

    void Remove(Guid clinic);

    IReadOnlyList<LedgerEntry> Pending();
}

/// <summary>Sem arquivo configurado (desenvolvimento e testes): o registro vive só enquanto o processo vive.</summary>
internal sealed class InMemoryClinicLedger : IClinicLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, LedgerEntry> _entries = [];

    public void Add(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries[entry.Clinic] = entry;
        }
    }

    public void Remove(Guid clinic)
    {
        lock (_gate)
        {
            _entries.Remove(clinic);
        }
    }

    public IReadOnlyList<LedgerEntry> Pending()
    {
        lock (_gate)
        {
            return [.. _entries.Values];
        }
    }
}

/// <summary>
/// Registro em arquivo JSON num volume do container. Cada mudança regrava o arquivo inteiro (são no máximo algumas
/// dezenas de entradas) num temporário e troca pelo nome final, então uma queda no meio deixa o arquivo antigo ou o
/// novo, nunca um pela metade. Um arquivo ilegível vira erro na subida, em vez de esquecer clínicas em silêncio.
/// </summary>
internal sealed class FileClinicLedger : IClinicLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly Dictionary<Guid, LedgerEntry> _entries;

    public FileClinicLedger(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _entries = Load(_path).ToDictionary(entry => entry.Clinic);
    }

    public void Add(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries[entry.Clinic] = entry;
            Save();
        }
    }

    public void Remove(Guid clinic)
    {
        lock (_gate)
        {
            if (_entries.Remove(clinic))
            {
                Save();
            }
        }
    }

    public IReadOnlyList<LedgerEntry> Pending()
    {
        lock (_gate)
        {
            return [.. _entries.Values];
        }
    }

    private static List<LedgerEntry> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<LedgerEntry>>(stream, Json) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"O registro de clínicas do Demo em {path} está ilegível. Confira o arquivo antes de apagar.", exception);
        }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, _entries.Values.OrderBy(entry => entry.CreatedAt).ToList(), Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
    }
}
