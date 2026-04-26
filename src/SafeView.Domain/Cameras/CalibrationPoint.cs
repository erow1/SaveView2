namespace SafeView.Domain.Cameras;

/// <summary>
/// Punkt kalibracji homografii kamery.
///  • <see cref="PixelX"/>, <see cref="PixelY"/> — pozycja znormalizowana [0..1] względem klatki kamery
///    (taka sama konwencja jak dla ROI/Zone/Detection).
///  • <see cref="WorldX"/>, <see cref="WorldY"/> — koordynaty w metrach w stałym układzie odniesienia
///    (np. (0,0) = narożnik hali). User wybiera konwencję; aplikacja ją tylko przechowuje.
///
/// Minimum 4 punkty (preferencyjnie nie-koliniarne) są potrzebne do policzenia macierzy 3x3
/// transformacji perspektywicznej obraz -> podłoga.
/// </summary>
public sealed class CalibrationPoint
{
    public double PixelX { get; set; }
    public double PixelY { get; set; }
    public double WorldX { get; set; }
    public double WorldY { get; set; }
}
