namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Auto-provisioning ROI + Zone "pełna klatka" dla kamer typu <c>CameraTransport.Api</c>.
/// Push-based kamera ma jedną pełnokadrową ROI + jedną pełnokadrową Zone — provider sam decyduje
/// co przesyła (już zrobił własny ROI/segmentation), więc nie filtrujemy po stronie SafeView.
///
/// Idempotentne: drugie wywołanie dla tej samej kamery nie tworzy duplikatów.
///
/// Wywoływane z UI po zapisie kamery z <c>Transport=Api</c>.
/// </summary>
public interface IApiCameraProvisioner
{
    Task EnsureFullFrameRoiAndZoneAsync(string cameraId, CancellationToken ct = default);
}
