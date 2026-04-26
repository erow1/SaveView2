namespace SafeView.Cameras;

/// <summary>
/// Konfiguracja sidecar-a MediaMTX. Gdy <see cref="Enabled"/> == false — pipeline omija gateway
/// i FFmpeg łączy się bezpośrednio do <c>Camera.SourceUrl</c>.
/// </summary>
public sealed class MediaMtxOptions
{
    public const string SectionName = "MediaMtx";

    public bool Enabled { get; set; }

    /// <summary>Host MediaMTX (bez schematu), domyślnie "localhost".</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Port RTSP (domyślnie 8554, zgodnie z konfiguracją MediaMTX).</summary>
    public int RtspPort { get; set; } = 8554;

    /// <summary>Port HLS (domyślnie 8888) — do odtwarzania w przeglądarce.</summary>
    public int HlsPort { get; set; } = 8888;

    /// <summary>Szablon URL-a RTSP — placeholdery: {host},{port},{path}.</summary>
    public string RtspUrlTemplate { get; set; } = "rtsp://{host}:{port}/{path}";

    /// <summary>Szablon URL-a HLS.</summary>
    public string HlsUrlTemplate { get; set; } = "http://{host}:{port}/{path}/index.m3u8";

    // ── Auto-management (wbudowany supervisor procesu) ────────────────────────
    /// <summary>Czy wbudowany supervisor ma startować MediaMTX jako proces dziecka aplikacji.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Ścieżka do binarki mediamtx; null = "mediamtx" w PATH.</summary>
    public string? BinaryPath { get; set; }

    /// <summary>Katalog roboczy, w którym zapisujemy mediamtx.yml. Null = {storage}/mediamtx.</summary>
    public string? ConfigDirectory { get; set; }

    /// <summary>Ścieżka do binarki ffmpeg (używana w runOnDemand dla źródeł plikowych). Null = "ffmpeg" w PATH.</summary>
    public string? FfmpegBinaryPath { get; set; }

    /// <summary>Co ile sekund supervisor sprawdza czy lista kamer zmieniła konfigurację.</summary>
    public int ReconcileIntervalSeconds { get; set; } = 30;
}

public sealed class FfmpegOptions
{
    public const string SectionName = "Ffmpeg";

    /// <summary>
    /// Absolutna ścieżka do binarki ffmpeg. Gdy null — zakładamy, że jest w PATH.
    /// Na macOS zwykle "/opt/homebrew/bin/ffmpeg" (Homebrew Apple Silicon) lub "/usr/local/bin/ffmpeg".
    /// </summary>
    public string? BinaryPath { get; set; }

    /// <summary>Timeout pojedynczej operacji snapshot/clip w sekundach.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Transport preferowany dla RTSP (tcp zwykle pewniejszy w sieci przemysłowej).</summary>
    public string RtspTransport { get; set; } = "tcp";
}
