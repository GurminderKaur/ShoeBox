using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shoebox;

/// <summary>
/// Everything lives in one folder on this computer:
///   receipts.json   all receipt data
///   settings.json   GST registration and business-use percentages
///   files/          the original images
/// No database, no cloud. Back it up like any other folder.
/// </summary>
public sealed class Store
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _lock = new();
    private readonly List<Receipt> _receipts;
    public string Dir { get; }
    public string FilesDir => Path.Combine(Dir, "files");
    public Settings Settings { get; private set; }

    public Store(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(FilesDir);
        _receipts = Load<List<Receipt>>("receipts.json") ?? new();
        Settings = Load<Settings>("settings.json") ?? new();
        // Anything left "processing" from a previous run gets queued again.
        foreach (var r in _receipts.Where(r => r.Status == ReceiptStatus.Processing)) r.Error = null;
    }

    public List<Receipt> All() { lock (_lock) return _receipts.Select(Clone).ToList(); }
    public Receipt? Get(string id) { lock (_lock) return _receipts.FirstOrDefault(r => r.Id == id) is { } r ? Clone(r) : null; }
    public List<string> Pending() { lock (_lock) return _receipts.Where(r => r.Status == ReceiptStatus.Processing).Select(r => r.Id).ToList(); }

    public Receipt Add(string originalName, byte[] data)
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        var stored = $"{id}{ext}";
        File.WriteAllBytes(Path.Combine(FilesDir, stored), data);
        var r = new Receipt { Id = id, FileName = Path.GetFileName(originalName), StoredFile = stored, AddedUtc = DateTime.UtcNow };
        lock (_lock) { _receipts.Add(r); SaveReceipts(); }
        return Clone(r);
    }

    public void Update(Receipt r)
    {
        lock (_lock)
        {
            var i = _receipts.FindIndex(x => x.Id == r.Id);
            if (i < 0) return;
            _receipts[i] = Clone(r);
            SaveReceipts();
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var r = _receipts.FirstOrDefault(x => x.Id == id);
            if (r == null) return false;
            _receipts.Remove(r);
            try { File.Delete(Path.Combine(FilesDir, r.StoredFile)); } catch { }
            SaveReceipts();
            return true;
        }
    }

    public void SaveSettings(Settings s)
    {
        lock (_lock)
        {
            Settings = s;
            Write("settings.json", s);
        }
    }

    private void SaveReceipts() => Write("receipts.json", _receipts);

    private void Write<T>(string name, T value)
    {
        var path = Path.Combine(Dir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true); // atomic replace: never leaves a half-written file
    }

    private T? Load<T>(string name)
    {
        var path = Path.Combine(Dir, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
    }

    private static Receipt Clone(Receipt r) => JsonSerializer.Deserialize<Receipt>(JsonSerializer.Serialize(r, Json), Json)!;
}
