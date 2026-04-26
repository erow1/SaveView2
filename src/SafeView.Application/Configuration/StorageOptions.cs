namespace SafeView.Application.Configuration;

/// <summary>
/// Konfiguracja ścieżek do fizycznych katalogów na dysku.
/// W bazie zapisujemy tylko relatywne ścieżki, pliki trzymamy na dysku (nie w base64).
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Korzeń — bazowa ścieżka; inne ścieżki mogą być absolutne lub relatywne względem Root.</summary>
    public string Root { get; set; } = "storage";

    public string Frames { get; set; } = "frames";
    public string Clips { get; set; } = "clips";
    public string Reports { get; set; } = "reports";
    public string Models { get; set; } = "models";
    public string Uploads { get; set; } = "uploads";
    public string Licenses { get; set; } = "licenses";

    /// <summary>Katalog na pliki wideo / obrazy używane jako źródło kamery (upload z UI).</summary>
    public string CameraMedia { get; set; } = "camera-media";

    /// <summary>Katalog na crop-y referencyjne visual-prompt klas detekcji (Faza 6).
    /// Ścieżki: <c>detection-classes/{classId}/refs/{fileName}.jpg</c>.</summary>
    public string DetectionClassRefs { get; set; } = "detection-classes";
}
