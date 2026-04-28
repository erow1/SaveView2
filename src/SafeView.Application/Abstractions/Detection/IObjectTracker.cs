using SafeView.Domain.Cameras;

namespace SafeView.Application.Abstractions.Detection;

/// <summary>
/// Info o tracku doklejone do detekcji po przejściu przez tracker. <c>null</c> przed wpięciem
/// trackera lub gdy detekcja nie ma jeszcze rzutu metrycznego (brak homografii kamery).
///
/// Wszystkie wartości fizyczne są w jednostkach SI: pozycja w metrach względem origin
/// homografii, prędkość w m/s, heading w stopniach (0 = +X osi metrycznej, 90 = +Y, CCW).
/// </summary>
public sealed record TrackedInfo(
    string TrackId,
    int SamplesInTrack,
    double FloorX,
    double FloorY,
    double? SpeedMps,
    double? HeadingDegrees);

/// <summary>
/// Prosty centroid-tracker per-kamera. Używa homografii do projekcji stopy bboxa na podłogę
/// w metrach, a potem greedy nearest-neighbor matchuje detekcje tej samej klasy między klatkami.
/// Tracker jest singletonem — trzyma stan ConcurrentDictionary&lt;cameraId, ...&gt;.
///
/// Eviction: tracki bez aktualizacji powyżej skonfigurowanego TTL są usuwane.
/// Uwaga: 1 fps + greedy matching gubi tożsamość przy bliskim mijaniu się dwóch obiektów
/// tej samej klasy (akceptowalne dla wykrywania kierunku, niedostateczne dla prawdziwego ID-tracking).
/// </summary>
public interface IObjectTracker
{
    /// <summary>
    /// Aktualizuje stan trackera dla kamery i zwraca dict (DetectionResult → TrackedInfo).
    /// Klucze są referencjami do tych samych instancji <see cref="DetectionResult"/> które
    /// caller przekazał — używa <see cref="System.Collections.Generic.ReferenceEqualityComparer"/>.
    /// </summary>
    /// <param name="cameraId">ID kamery — separate state per camera.</param>
    /// <param name="frameTimestamp">Timestamp tej klatki (UTC).</param>
    /// <param name="detections">Wszystkie detekcje z klatki (już znormalizowane do [0..1]).</param>
    /// <param name="homography">Homografia kamery. Gdy null, tracker zwraca pusty dict (nic nie da się policzyć w metrach).</param>
    IReadOnlyDictionary<DetectionResult, TrackedInfo> UpdateAndEnrich(
        string cameraId,
        DateTime frameTimestamp,
        IReadOnlyList<DetectionResult> detections,
        HomographyMatrix? homography);

    /// <summary>Czyści cały stan (np. przy zmianie konfiguracji).</summary>
    void Reset();
}
