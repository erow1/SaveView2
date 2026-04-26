using SafeView.Domain.Common;

namespace SafeView.Domain.Zones;

/// <summary>Punkt wielokąta w znormalizowanych koordynatach [0..1] względem rozmiaru klatki.</summary>
public sealed class ZonePoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// Strefa — wielokąt wewnątrz ROI, w którym aplikowane są triggery.
///
/// Relacje:
///  • Zone → ROI (<see cref="RoiId"/>) — strefa MUSI należeć do jednego ROI
///  • Zone → Camera (<see cref="CameraId"/>) — denormalizacja dla szybkich query (camera = roi.camera)
///  • Zone → Triggers (<see cref="TriggerIds"/>) — multi-select globalnych triggerów
///
/// Walidacja geometryczna: wszystkie punkty <see cref="Polygon"/> muszą leżeć wewnątrz
/// <c>Roi.Rectangle</c> (sprawdzane w UI i przed zapisem w repozytorium).
/// </summary>
public sealed class Zone : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Kamera do której należy strefa (= roi.cameraId, denormalizowane).</summary>
    public string CameraId { get; set; } = string.Empty;

    /// <summary>ROI w którym leży ta strefa. Wymagane — strefy bez ROI są nieaktywne.</summary>
    public string RoiId { get; set; } = string.Empty;

    /// <summary>Wierzchołki poligonu w kolejności. Minimalnie 3. Koordynaty [0..1] kamery.</summary>
    public List<ZonePoint> Polygon { get; set; } = [];

    /// <summary>ID triggerów (globalnych, współdzielonych) aktywnych w tej strefie.</summary>
    public List<string> TriggerIds { get; set; } = [];

    public bool Enabled { get; set; } = true;

    /// <summary>Kolor overlay w UI (hex, np. "#FF5964").</summary>
    public string Color { get; set; } = "#FF5964";
}
