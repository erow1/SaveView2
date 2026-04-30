namespace SafeView.Application.Abstractions.Storage;

/// <summary>
/// Abstrakcja składowania plików binarnych (frames, clips, reports, models, uploads).
/// Implementacja zapisuje fizycznie na dysku w konfigurowalnych ścieżkach; zwraca ścieżkę logiczną do zapisu w bazie.
/// </summary>
public interface IFileStore
{
    /// <summary>Zapisuje strumień i zwraca ścieżkę logiczną (relatywną do {StorageRoot}).</summary>
    Task<string> SaveAsync(FileKind kind, string relativePath, Stream content, CancellationToken ct = default);

    /// <summary>Otwiera plik do odczytu; null jeśli nie istnieje.</summary>
    Task<Stream?> OpenReadAsync(FileKind kind, string relativePath, CancellationToken ct = default);

    /// <summary>Usuwa plik; true jeśli istniał.</summary>
    Task<bool> DeleteAsync(FileKind kind, string relativePath, CancellationToken ct = default);

    /// <summary>Zwraca absolutną ścieżkę pliku (do użycia przez FFmpeg / ONNX Runtime).</summary>
    string ResolveAbsolutePath(FileKind kind, string relativePath);

    /// <summary>Czy plik istnieje.</summary>
    bool Exists(FileKind kind, string relativePath);
}

public enum FileKind
{
    Frame,
    Clip,
    Report,
    Model,
    Upload,
    License,
    /// <summary>Pliki wideo / obrazy używane jako źródło sygnału kamery (MP4, MKV, AVI, JPG, PNG).</summary>
    CameraMedia,
    /// <summary>Crop-y referencyjne dla visual-prompt klas detekcji (Faza 6). Ścieżki tworzą
    /// hierarchię <c>detection-classes/{classId}/refs/{fileName}.jpg</c>.</summary>
    DetectionClassRef
}
