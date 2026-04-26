using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SafeView.Application.Abstractions.Media;
using SafeView.Application.Abstractions.Storage;
using SafeView.Cameras.Vendors;
using SafeView.Domain.Cameras;

namespace SafeView.Cameras;

/// <summary>
/// Implementacja ISnapshotService oparta o bezpośrednie wywołanie ffmpeg (bez FFMpegCore,
/// żeby mieć pełną kontrolę nad flagami -rtsp_transport, -stimeout itd.).
/// </summary>
public sealed class FfmpegSnapshotService : ISnapshotService
{
    private readonly IFileStore _files;
    private readonly IOptionsMonitor<FfmpegOptions> _ffmpeg;
    private readonly IOptionsMonitor<MediaMtxOptions> _mtx;
    private readonly ILogger<FfmpegSnapshotService> _log;

    public FfmpegSnapshotService(
        IFileStore files,
        IOptionsMonitor<FfmpegOptions> ffmpeg,
        IOptionsMonitor<MediaMtxOptions> mtx,
        ILogger<FfmpegSnapshotService> log)
    {
        _files = files;
        _ffmpeg = ffmpeg;
        _mtx = mtx;
        _log = log;
    }

    public async Task<SnapshotResult> CaptureAsync(Camera camera, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(camera);

        var now = DateTime.UtcNow;
        var relPath = Path.Combine(
            camera.Id,
            now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            $"{now:HHmmss}_{Guid.NewGuid():N}.jpg".Replace(':', '-'));
        var absPath = _files.ResolveAbsolutePath(FileKind.Frame, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

        // ── Fast path: HTTP snapshot endpoint (DAHUA/Hikvision/Axis) ─────────────
        // Omija RTSP handshake + oczekiwanie na keyframe → 200-500ms zamiast 2-4s.
        var httpUrl = VendorRtspBuilder.BuildHttpSnapshotUrl(camera);
        if (httpUrl is not null)
        {
            var httpOk = await TryHttpSnapshotAsync(httpUrl, absPath, ct).ConfigureAwait(false);
            if (httpOk)
            {
                var sizeHttp = new FileInfo(absPath).Length;
                return new SnapshotResult(true, relPath, absPath, null, now, sizeHttp);
            }
            _log.LogDebug("HTTP snapshot failed for {Camera}, falling back to ffmpeg/RTSP.", camera.Name);
        }

        // ── Fallback: ffmpeg via direct camera RTSP (bypassuje MediaMTX) ────────
        var input = ResolveInputUrl(camera);
        var args = BuildSnapshotArgs(input, absPath);

        var (ok, stderr) = await RunFfmpegAsync(args, ct).ConfigureAwait(false);
        if (!ok || !File.Exists(absPath))
        {
            _log.LogWarning("Snapshot failed for camera {Camera}: {Err}", camera.Name, stderr);
            return new SnapshotResult(false, null, null, stderr, now, 0);
        }

        var size = new FileInfo(absPath).Length;
        return new SnapshotResult(true, relPath, absPath, null, now, size);
    }

    /// <summary>
    /// Pobiera snapshot przez HTTP GET (vendor CGI endpoint). Obsługuje Basic i Digest auth
    /// (HttpClient + SocketsHttpHandler automatycznie negocjuje). Timeout 3s.
    /// </summary>
    private async Task<bool> TryHttpSnapshotAsync(string url, string outputPath, CancellationToken ct)
    {
        try
        {
            // Ekstraktujemy credentials z URL jeśli są (format http://user:pass@host/...),
            // bo .NET HttpClient nie wysyła ich automatycznie z URL.
            var (cleanUrl, creds) = ExtractCredentials(url);

            var handler = new HttpClientHandler
            {
                Credentials = creds,
                PreAuthenticate = false // pozwól na Digest challenge
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

            using var resp = await http.GetAsync(cleanUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!ctype.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogDebug("HTTP snapshot returned non-image content-type: {Ct}", ctype);
                return false;
            }

            await using var fs = File.Create(outputPath);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            return new FileInfo(outputPath).Length > 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "HTTP snapshot request failed for {Url}", url);
            return false;
        }
    }

    private static (string Url, ICredentials? Creds) ExtractCredentials(string url)
    {
        var uri = new Uri(url);
        if (string.IsNullOrEmpty(uri.UserInfo)) return (url, null);

        var parts = uri.UserInfo.Split(':', 2);
        var user = WebUtility.UrlDecode(parts[0]);
        var pass = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "";

        // Przebuduj URL bez user:pass
        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        return (builder.Uri.ToString(), new NetworkCredential(user, pass));
    }

    public async Task<ClipResult> CaptureClipAsync(Camera camera, int durationSeconds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (durationSeconds <= 0) durationSeconds = 10;

        var now = DateTime.UtcNow;
        var relPath = Path.Combine(
            camera.Id,
            now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            $"{now:HHmmss}_{Guid.NewGuid():N}.mp4".Replace(':', '-'));
        var absPath = _files.ResolveAbsolutePath(FileKind.Clip, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);

        var input = ResolveInputUrl(camera);
        var args = BuildClipArgs(input, absPath, durationSeconds);

        var (ok, stderr) = await RunFfmpegAsync(args, ct, extraTimeoutSeconds: durationSeconds + 5).ConfigureAwait(false);
        if (!ok || !File.Exists(absPath))
        {
            _log.LogWarning("Clip capture failed for {Camera}: {Err}", camera.Name, stderr);
            return new ClipResult(false, null, null, stderr, now, TimeSpan.Zero, 0);
        }

        return new ClipResult(true, relPath, absPath, null, now, TimeSpan.FromSeconds(durationSeconds),
            new FileInfo(absPath).Length);
    }

    public string? ResolveStreamUrl(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var mtx = _mtx.CurrentValue;
        if (!mtx.Enabled || string.IsNullOrWhiteSpace(camera.MediaMtxPath))
            return null;
        return mtx.HlsUrlTemplate
            .Replace("{host}", mtx.Host, StringComparison.Ordinal)
            .Replace("{port}", mtx.HlsPort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{path}", camera.MediaMtxPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rozwiązuje URL wejściowy dla ffmpeg (snapshot/klip).
    ///
    /// Celowo **omija MediaMTX** — łączymy się bezpośrednio do kamery, bo:
    ///  • MediaMTX ma sourceOnDemand → pierwszy snapshot po bezczynności ma cold-start ~1-2s
    ///  • Pośrednik dodaje dodatkowy RTSP handshake
    /// MediaMTX nadal jest używany dla live HLS viewing w przeglądarce (wielu klientów).
    ///
    /// Dla kamer plikowych — zwracamy lokalną ścieżkę (MediaMTX streamuje te przez runOnDemand
    /// do klientów HLS, ale ffmpeg snapshot czyta plik bezpośrednio).
    /// </summary>
    private static string ResolveInputUrl(Camera camera)
    {
        if (camera.Transport == CameraTransport.File || IsLocalFile(camera.SourceUrl))
            return camera.SourceUrl;

        // Dla kamer sieciowych: użyj vendor-template jeśli jest Host, inaczej raw SourceUrl.
        var built = VendorRtspBuilder.Build(camera);
        return string.IsNullOrWhiteSpace(built) ? camera.SourceUrl : built;
    }

    private List<string> BuildSnapshotArgs(string input, string outputPath)
    {
        var ff = _ffmpeg.CurrentValue;
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        AddFastStartArgs(args, input);
        AddNetworkTimeoutArgs(args, input, ff);
        args.AddRange([
            "-i", input,
            "-frames:v", "1",
            "-q:v", "3", // jakość JPG (2-5 rozsądne)
            "-y",
            outputPath
        ]);
        return args;
    }

    /// <summary>
    /// Fast-start flagi ffmpeg — ograniczają czas probingu i buforowania przy
    /// otwieraniu strumienia sieciowego. Potrafi ściąć 1-2s z cold-startu.
    ///  • -probesize 500k    — wystarcza do złapania SPS/PPS H.264; domyślnie 5MB
    ///  • -analyzeduration 0 — nie analizuj czasu trwania; zacznij dekodować natychmiast
    ///  • -fflags nobuffer   — nie buforuj pakietów
    ///  • -flags low_delay   — tryb low-latency dekodera
    /// Dla lokalnych plików — pomijamy (żeby nie psuć seeków).
    /// </summary>
    private static void AddFastStartArgs(List<string> args, string input)
    {
        if (IsLocalFile(input)) return;
        args.AddRange([
            "-probesize", "500000",
            "-analyzeduration", "0",
            "-fflags", "nobuffer",
            "-flags", "low_delay",
        ]);
    }

    private List<string> BuildClipArgs(string input, string outputPath, int durationSeconds)
    {
        var ff = _ffmpeg.CurrentValue;
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        AddFastStartArgs(args, input);
        AddNetworkTimeoutArgs(args, input, ff);
        args.AddRange([
            "-i", input,
            "-t", durationSeconds.ToString(CultureInfo.InvariantCulture),
            "-c", "copy", // bez rekodowania — znacznie szybsze i bez obciążenia CPU
            "-y",
            outputPath
        ]);
        return args;
    }

    /// <summary>
    /// Dobiera flagi timeout/transport zależnie od protokołu wejścia.
    /// W ffmpeg 7.x:
    ///  - RTSP: <c>-rtsp_transport</c> + <c>-timeout</c> (μs) — demuxer-specific
    ///  - HTTP/RTMP/inne sieciowe: <c>-rw_timeout</c> (μs)
    ///  - Lokalny plik: nic (brak sensu timeoutu)
    /// </summary>
    private static void AddNetworkTimeoutArgs(List<string> args, string input, FfmpegOptions ff)
    {
        if (IsLocalFile(input)) return;

        if (IsRtsp(input))
        {
            args.AddRange(["-rtsp_transport", ff.RtspTransport]);
            args.AddRange(["-timeout", "5000000"]); // RTSP demuxer socket I/O timeout (μs)
        }
        else
        {
            args.AddRange(["-rw_timeout", "5000000"]); // generic I/O timeout (HTTP/TCP)
        }
    }

    private static bool IsRtsp(string url) =>
        url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalFile(string url) =>
        !url.Contains("://", StringComparison.Ordinal) || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

    private async Task<(bool Success, string? Stderr)> RunFfmpegAsync(
        IEnumerable<string> args, CancellationToken ct, int? extraTimeoutSeconds = null)
    {
        var ff = _ffmpeg.CurrentValue;
        var binary = BundledBinaries.Resolve(ff.BinaryPath, "ffmpeg", "ffmpeg");

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
                return (false, "ffmpeg: failed to start process");

            var timeout = TimeSpan.FromSeconds(ff.TimeoutSeconds + (extraTimeoutSeconds ?? 0));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, $"ffmpeg: timeout after {timeout.TotalSeconds:N0}s");
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            return proc.ExitCode == 0
                ? (true, null)
                : (false, string.IsNullOrWhiteSpace(stderr) ? $"exit={proc.ExitCode}" : stderr.Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.LogError(ex, "ffmpeg binary not found at '{Binary}'. Install ffmpeg or set Ffmpeg:BinaryPath.", binary);
            return (false, $"ffmpeg not found: {binary}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ffmpeg invocation failed");
            return (false, ex.Message);
        }
    }
}
