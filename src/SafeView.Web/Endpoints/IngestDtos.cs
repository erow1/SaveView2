using System.Text.Json.Serialization;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Request body dla ingest endpoint (Faza ApiCamera, schema 1.0).
/// W multipart wariancie wysyłany jako pole formularza <c>metadata</c> (application/json).
/// W JSON-only wariancie cały body, plus jedno z: <see cref="ImageBase64"/> | <see cref="ImageUrl"/>.
/// </summary>
public sealed class IngestMetadataDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("frame")]
    public IngestFrameDto Frame { get; set; } = new();

    [JsonPropertyName("detections")]
    public List<IngestDetectionDto> Detections { get; set; } = [];

    [JsonPropertyName("source")]
    public IngestSourceDto? Source { get; set; }

    /// <summary>JSON-only fallback — image jako base64 (33% size overhead vs multipart).</summary>
    [JsonPropertyName("image_base64")]
    public string? ImageBase64 { get; set; }

    /// <summary>JSON-only fallback — URL z którego serwer pobierze obraz (max 10MB, 5s timeout).</summary>
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }
}

public sealed class IngestFrameDto
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>ISO 8601 UTC, czas faktycznej obserwacji od sendera (server może clamp-ować jeśli &gt; ±5min od now).</summary>
    [JsonPropertyName("captured_at")]
    public DateTime CapturedAt { get; set; }

    /// <summary>Optional UUID — server używa do dedup (retry-safe, returns 200 z already_processed).</summary>
    [JsonPropertyName("frame_id")]
    public string? FrameId { get; set; }
}

public sealed class IngestDetectionDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>Bbox w pixel coords, top-left origin. Server normalizuje do [0..1] względem frame W/H.</summary>
    [JsonPropertyName("bbox")]
    public IngestBboxDto Bbox { get; set; } = new();

    [JsonPropertyName("track_id")]
    public string? TrackId { get; set; }

    [JsonPropertyName("polygon")]
    public List<IngestPointDto>? Polygon { get; set; }

    [JsonPropertyName("keypoints")]
    public List<IngestKeypointDto>? Keypoints { get; set; }

    /// <summary>Sender-specific attributes (np. helmet_color, vehicle_plate). Pipeline ignoruje, audit zachowuje.</summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, object>? Attributes { get; set; }
}

public sealed class IngestBboxDto
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public sealed class IngestPointDto
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}

public sealed class IngestKeypointDto
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }
}

public sealed class IngestSourceDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("inference_ms")]
    public double? InferenceMs { get; set; }
}

/// <summary>Response 200 OK po udanym ingest.</summary>
public sealed class IngestResponseDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "accepted";

    [JsonPropertyName("frame_id")]
    public string? FrameId { get; set; }

    [JsonPropertyName("detections_count")]
    public int DetectionsCount { get; set; }

    [JsonPropertyName("processing_ms")]
    public long ProcessingMs { get; set; }
}
