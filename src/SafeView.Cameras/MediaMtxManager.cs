using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Cameras.Vendors;
using SafeView.Domain.Cameras;

namespace SafeView.Cameras;

/// <summary>
/// Sidecar-supervisor MediaMTX: generuje mediamtx.yml z listy kamer,
/// startuje proces, restartuje przy zmianie configu albo gdy padnie.
/// Singleton — trzyma referencję do uruchomionego procesu.
/// </summary>
public sealed class MediaMtxManager : IAsyncDisposable
{
    private readonly IOptionsMonitor<MediaMtxOptions> _opts;
    private readonly ILogger<MediaMtxManager> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private string? _currentConfigHash;
    private string? _currentConfigPath;
    private DateTime? _startedAt;
    private string? _lastError;

    public MediaMtxManager(IOptionsMonitor<MediaMtxOptions> opts, ILogger<MediaMtxManager> log)
    {
        _opts = opts;
        _log = log;
    }

    public bool IsRunning => _process is { HasExited: false };
    public DateTime? StartedAt => _startedAt;
    public string? CurrentConfigPath => _currentConfigPath;
    public string? LastError => _lastError;

    /// <summary>Ścieżka do generowanego mediamtx.yml (tworzy katalog jeśli trzeba).</summary>
    public string ResolveConfigPath()
    {
        var opts = _opts.CurrentValue;
        var dir = string.IsNullOrWhiteSpace(opts.ConfigDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "storage", "mediamtx")
            : opts.ConfigDirectory!;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "mediamtx.yml");
    }

    /// <summary>
    /// Zapewnia, że proces jest uruchomiony z aktualną konfiguracją.
    /// Wywoływane okresowo przez <see cref="MediaMtxRunner"/>.
    /// </summary>
    public async Task ReconcileAsync(IReadOnlyList<Camera> cameras, CancellationToken ct = default)
    {
        var opts = _opts.CurrentValue;
        if (!opts.Enabled || !opts.AutoStart)
        {
            await StopAsync().ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var yaml = BuildYaml(cameras, opts);
            var hash = Sha256(yaml);
            var configPath = ResolveConfigPath();

            var configChanged = hash != _currentConfigHash;
            var processDead = _process is null || _process.HasExited;

            if (configChanged)
            {
                await File.WriteAllTextAsync(configPath, yaml, ct).ConfigureAwait(false);
                _currentConfigHash = hash;
                _currentConfigPath = configPath;
                _log.LogInformation("MediaMTX config written: {Path} ({Cameras} kamer)", configPath, cameras.Count);
            }

            if (processDead || configChanged)
            {
                await StopProcessAsync().ConfigureAwait(false);
                StartProcess(opts, configPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopProcessAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private void StartProcess(MediaMtxOptions opts, string configPath)
    {
        var binary = BundledBinaries.Resolve(opts.BinaryPath, "mediamtx", "mediamtx");
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(configPath);

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            { if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogDebug("[mediamtx] {Line}", e.Data); };
            proc.ErrorDataReceived += (_, e) =>
            { if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogInformation("[mediamtx] {Line}", e.Data); };
            proc.Exited += (_, _) =>
                _log.LogWarning("MediaMTX process exited (code={Code}). Supervisor zrestartuje przy następnym tick.",
                    proc.HasExited ? proc.ExitCode : -1);

            if (!proc.Start())
            {
                _lastError = $"Nie udało się uruchomić procesu '{binary}'.";
                _log.LogError("MediaMTX start failed: {Error}", _lastError);
                return;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _process = proc;
            _startedAt = DateTime.UtcNow;
            _lastError = null;
            _log.LogInformation("MediaMTX uruchomiony (pid={Pid}, binary={Binary})", proc.Id, binary);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _lastError = $"MediaMTX binary nieznaleziony: {binary}. Zainstaluj mediamtx albo ustaw MediaMtx:BinaryPath. ({ex.Message})";
            _log.LogError(ex, "MediaMTX binary not found: {Binary}", binary);
        }
        catch (Exception ex)
        {
            _lastError = $"Błąd startu MediaMTX: {ex.Message}";
            _log.LogError(ex, "MediaMTX start failed");
        }
    }

    private async Task StopProcessAsync()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MediaMTX stop error");
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _startedAt = null;
        }
    }

    /// <summary>Generator pliku konfiguracyjnego — minimalny YAML zgodny z MediaMTX 1.x.</summary>
    public static string BuildYaml(IReadOnlyList<Camera> cameras, MediaMtxOptions opts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Wygenerowane automatycznie przez SafeView — nie edytuj ręcznie.");
        // UWAGA: nie dodawaj tu dynamicznego timestampu — hash YAML-a jest używany do wykrywania
        // zmian konfiguracji; zmienny timestamp powodował restart MediaMTX co cykl reconcile (30s).
        sb.AppendLine();
        sb.AppendLine("logLevel: info");
        sb.AppendLine("logDestinations: [stdout]");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"rtspAddress: :{opts.RtspPort}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"hlsAddress: :{opts.HlsPort}");
        sb.AppendLine("hls: yes");
        // UWAGA: nie dodajemy custom opcji hlsSegmentCount/hlsSegmentDuration/hlsAlwaysRemux
        // bo ich kombinacja z mpegts/fmp4 w MediaMTX 1.17.x powodowała 404 na segmentach.
        // Używamy domyślnych wartości (lowLatency fmp4, 7 segmentów × 1s, hlsAlwaysRemux: false).
        sb.AppendLine();
        sb.AppendLine("paths:");

        var any = false;
        foreach (var c in cameras.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.MediaMtxPath)))
        {
            var url = VendorRtspBuilder.Build(c);
            if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(c.SourceUrl))
                url = c.SourceUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;

            var path = SanitizePath(c.MediaMtxPath!);
            var isFile = c.Transport == CameraTransport.File || IsLocalFilePath(url);

            sb.AppendLine(CultureInfo.InvariantCulture, $"  {path}:");

            if (isFile)
            {
                // MediaMTX cannot accept ffmpeg CLI flags in a source: ffmpeg:// URI.
                // Instead, use runOnDemand to launch ffmpeg when a viewer connects.
                // ffmpeg pushes the file as RTSP back into MediaMTX.
                var isImage = IsImageFile(url);
                var ffBinary = BundledBinaries.Resolve(opts.FfmpegBinaryPath, "ffmpeg", "ffmpeg");
                var ffmpegCmd = isImage
                    ? $"{ffBinary} -hide_banner -loglevel error -loop 1 -framerate 1 -re -i {url} -c:v libx264 -preset ultrafast -tune stillimage -pix_fmt yuv420p -f rtsp rtsp://localhost:{opts.RtspPort}/$MTX_PATH"
                    : $"{ffBinary} -hide_banner -loglevel error -stream_loop -1 -re -i {url} -c copy -f rtsp rtsp://localhost:{opts.RtspPort}/$MTX_PATH";
                sb.AppendLine("    source: publisher");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    runOnDemand: {ffmpegCmd}");
                sb.AppendLine("    runOnDemandRestart: yes");
                sb.AppendLine("    runOnDemandCloseAfter: 10s");
            }
            else
            {
                // UWAGA: bez sourceOnDemand — MediaMTX trzyma otwarty RTSP do kamery 24/7,
                // dzięki czemu pierwszy request HLS zawsze zwraca segmenty (bez cold-startu).
                // Alternatywa (oszczędność pasma): włącz sourceOnDemand ale licz się z 2-5s opóźnieniem
                // przy pierwszym otwarciu Live Preview.
                sb.AppendLine(CultureInfo.InvariantCulture, $"    source: {url}");
                sb.AppendLine("    sourceProtocol: tcp");
            }

            any = true;
        }

        if (!any)
        {
            sb.AppendLine("  # (brak aktywnych kamer — placeholder)");
            sb.AppendLine("  _placeholder:");
            sb.AppendLine("    source: publisher");
        }

        return sb.ToString();
    }

    /// <summary>MediaMTX path: tylko [a-zA-Z0-9_], max 64 znaki.</summary>
    public static string SanitizePath(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
                sb.Append(ch);
            else
                sb.Append('_');
        }
        var s = sb.ToString();
        if (s.Length == 0) s = "cam";
        if (char.IsDigit(s[0])) s = "c" + s;
        return s.Length > 64 ? s[..64] : s;
    }

    private static bool IsLocalFilePath(string url) =>
        !url.Contains("://", StringComparison.Ordinal) || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

    private static bool IsImageFile(string url)
    {
        var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? url[7..] : url;
        var ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
