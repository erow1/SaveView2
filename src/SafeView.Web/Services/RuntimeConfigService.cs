using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;

namespace SafeView.Web.Services;

/// <summary>
/// Edytor pliku <c>appsettings.Runtime.json</c> — pozwala z poziomu UI nadpisywać
/// dowolne klucze konfiguracji (MediaMtx, Ffmpeg, LLM, Smtp, Storage…) bez restartu.
/// Zapis atomowy (temp + rename). <see cref="IConfiguration"/> reloadOnChange sam
/// podchwytuje plik, a <see cref="IOptionsMonitor{T}"/> rozpropaguje zmiany.
/// </summary>
public sealed class RuntimeConfigService : IDisposable
{
    public void Dispose() => _gate.Dispose();

    private readonly string _path;
    private readonly ILogger<RuntimeConfigService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RuntimeConfigService(IHostEnvironment env, ILogger<RuntimeConfigService> log)
    {
        _log = log;
        // ContentRoot = katalog projektu w dev, katalog aplikacji w prod.
        _path = Path.Combine(env.ContentRootPath, "appsettings.Runtime.json");
    }

    public string FilePath => _path;

    /// <summary>Surowa zawartość pliku (na potrzeby edytora JSON).</summary>
    public async Task<string> ReadRawAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return "{}";
        return await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
    }

    /// <summary>Załaduj jako drzewo JSON (writable).</summary>
    public async Task<JsonObject> LoadAsync(CancellationToken ct = default)
    {
        var raw = await ReadRawAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        try
        {
            var node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return node as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "appsettings.Runtime.json nie jest poprawnym JSON — zwracam pustą strukturę");
            return new JsonObject();
        }
    }

    /// <summary>Pobierz wartość po ścieżce kropkowanej (np. "MediaMtx:BinaryPath" lub "MediaMtx.BinaryPath").</summary>
    public static JsonNode? GetByPath(JsonObject root, string dottedPath)
    {
        var parts = SplitPath(dottedPath);
        JsonNode? cur = root;
        foreach (var p in parts)
        {
            if (cur is JsonObject obj && obj.TryGetPropertyValue(p, out var next)) cur = next;
            else return null;
        }
        return cur;
    }

    /// <summary>Ustaw wartość po ścieżce kropkowanej. Null/empty usuwa klucz.</summary>
    public static void SetByPath(JsonObject root, string dottedPath, JsonNode? value)
    {
        var parts = SplitPath(dottedPath);
        if (parts.Length == 0) return;

        JsonObject cur = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var key = parts[i];
            if (cur[key] is JsonObject child)
            {
                cur = child;
            }
            else
            {
                var nxt = new JsonObject();
                cur[key] = nxt;
                cur = nxt;
            }
        }

        var leaf = parts[^1];
        if (value is null)
        {
            cur.Remove(leaf);
        }
        else
        {
            cur[leaf] = value;
        }
    }

    /// <summary>Atomowy zapis pliku (temp → rename).</summary>
    public async Task SaveAsync(JsonObject root, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            // File.Replace wymaga istniejącego targetu — fallback na Move
            if (File.Exists(_path))
            {
                File.Replace(tmp, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmp, _path, overwrite: true);
            }
            _log.LogInformation("appsettings.Runtime.json zapisany ({Path})", _path);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Helper: ustaw pojedynczą wartość i zapisz.</summary>
    public async Task SetAndSaveAsync(string dottedPath, JsonNode? value, CancellationToken ct = default)
    {
        var root = await LoadAsync(ct).ConfigureAwait(false);
        SetByPath(root, dottedPath, value);
        await SaveAsync(root, ct).ConfigureAwait(false);
    }

    /// <summary>Batch: zapisz mapę ścieżka→wartość jednym writem.</summary>
    public async Task SetManyAndSaveAsync(IDictionary<string, JsonNode?> values, CancellationToken ct = default)
    {
        var root = await LoadAsync(ct).ConfigureAwait(false);
        foreach (var kv in values) SetByPath(root, kv.Key, kv.Value);
        await SaveAsync(root, ct).ConfigureAwait(false);
    }

    private static string[] SplitPath(string dottedPath)
        => (dottedPath ?? "").Split([':', '.'], StringSplitOptions.RemoveEmptyEntries);
}
