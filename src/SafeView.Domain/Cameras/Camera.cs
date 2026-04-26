using SafeView.Domain.Common;

namespace SafeView.Domain.Cameras;

public sealed class Camera : Entity
{
    /// <summary>Nazwa wyświetlana, unikalna (np. "Hala A — wejście").</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Lokalizacja / strefa fizyczna (tekst, luźny).</summary>
    public string? Location { get; set; }

    /// <summary>Tagi swobodne do filtrowania w UI.</summary>
    public List<string> Tags { get; set; } = [];

    // ── Vendor + parametry połączenia ─────────────────────────────────────────
    /// <summary>Producent kamery — determinuje szablon RTSP-a. <see cref="CameraVendor.Custom"/> = ręczny SourceUrl.</summary>
    public CameraVendor Vendor { get; set; } = CameraVendor.Custom;

    /// <summary>Który strumień brać z kamery — main (HD, dla wzorcowych snapshotów / klipów) albo sub (LD, dla samplera ML).</summary>
    public CameraStreamProfile StreamProfile { get; set; } = CameraStreamProfile.Main;

    /// <summary>Adres IP / hostname kamery.</summary>
    public string? Host { get; set; }

    /// <summary>Port RTSP kamery (typowo 554, reCamera 8554).</summary>
    public int Port { get; set; } = 554;

    /// <summary>Login do RTSP (opcjonalny — niektóre kamery jak reCamera nie wymagają).</summary>
    public string? Username { get; set; }

    /// <summary>Hasło RTSP.</summary>
    public string? Password { get; set; }

    /// <summary>Kanał (np. Hikvision/Dahua channel 1..16). Domyślnie 1.</summary>
    public int Channel { get; set; } = 1;

    /// <summary>
    /// Źródło źródłowe (kamera → MediaMTX). Wyliczane z parametrów vendor-specific, z możliwością ręcznego override
    /// (gdy Vendor == Custom). Typowo rtsp://user:pass@host/stream.
    /// </summary>
    public string SourceUrl { get; set; } = string.Empty;

    public CameraTransport Transport { get; set; } = CameraTransport.Rtsp;

    /// <summary>
    /// Path w MediaMTX (np. "cam_abc"). Generowany automatycznie z Id przy zapisie kamery.
    /// Gdy null/empty — FFmpeg łączy się bezpośrednio do SourceUrl.
    /// </summary>
    public string? MediaMtxPath { get; set; }

    /// <summary>Czy kamera jest aktywna (branana pod uwagę przez sampler).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Częstotliwość próbkowania klatek do detekcji (s).</summary>
    public int SnapshotIntervalSeconds { get; set; } = 5;

    /// <summary>Domyślna długość klipu zachowywanego przy incydencie (s).</summary>
    public int IncidentClipSeconds { get; set; } = 10;

    /// <summary>Ostatnie udane próbkowanie (UTC).</summary>
    public DateTime? LastSnapshotAt { get; set; }

    /// <summary>Ostatni status — ułatwia diagnostykę w UI.</summary>
    public string? LastStatus { get; set; }

    /// <summary>
    /// Punkty kalibracji homografii — min. 4 nie-koliniarne punkty na płaszczyźnie podłogi.
    /// Puste = brak kalibracji, filtry przestrzenne dla tej kamery nie zadziałają.
    /// </summary>
    public List<CalibrationPoint> CalibrationPoints { get; set; } = [];

    /// <summary>
    /// Dla <see cref="CameraTransport.Api"/> — opcjonalny pin: tylko ten <c>ApiKey.Id</c> może
    /// pisać do tej kamery przez ingest endpoint. Null = każdy klucz ze scope <c>api:cameras:write</c>
    /// może pchać. Dodatkowy gate ponad samym scope (defense-in-depth).
    /// </summary>
    public string? IngestApiKeyId { get; set; }
}

public enum CameraTransport
{
    Rtsp,
    Http,
    Rtmp,
    File,
    /// <summary>
    /// Push-based: zewnętrzne oprogramowanie (kamera embedded, edge appliance, własny inference server)
    /// wysyła klatki + pre-computed detekcje przez REST API (<c>POST /api/v1/cameras/{id}/ingest</c>).
    /// Sampler nie pollu-je tej kamery; ROI/Strefa są auto-provisioned jako pełna klatka
    /// (provider sam decyduje co przesyła).
    /// </summary>
    Api
}

public enum CameraVendor
{
    /// <summary>Ręczny SourceUrl — dla nietypowych kamer / testów.</summary>
    Custom = 0,
    Dahua = 1,
    Hikvision = 2,
    Axis = 3,
    Bosch = 4,
    /// <summary>Hanwha / Samsung Techwin — kamery Wisenet.</summary>
    Samsung = 5,
    /// <summary>reCamera od Seeed Studio (open-source AI camera).</summary>
    ReCamera = 6,
    /// <summary>Plik lokalny (MP4/MKV/AVI w pętli) lub obraz (JPG/PNG) jako źródło sygnału.</summary>
    FileSource = 99,
    /// <summary>Push-based API source — zewnętrzny system wysyła klatki + detekcje przez REST
    /// (<c>POST /api/v1/cameras/{id}/ingest</c>). Wymusza <c>CameraTransport.Api</c>.</summary>
    ApiPush = 100
}

public enum CameraStreamProfile
{
    /// <summary>Strumień główny (zwykle 1080p+ H.264) — używany do snapshotów / klipów dowodowych.</summary>
    Main = 0,
    /// <summary>Strumień pomocniczy (sub-stream, niska rozdzielczość) — szybsza detekcja, mniej CPU.</summary>
    Sub = 1
}
