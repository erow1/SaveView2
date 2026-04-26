namespace SafeView.Domain.Detection;

/// <summary>
/// Prostokątny obszar w znormalizowanych koordynatach [0..1] względem rozmiaru klatki kamery.
/// <see cref="X"/>,<see cref="Y"/> = lewy-górny róg; <see cref="Width"/>,<see cref="Height"/> = rozmiar.
/// </summary>
public sealed class RoiRectangle
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>Czy współrzędne są w dopuszczalnym zakresie [0..1] i prostokąt ma sens.</summary>
    public bool IsValid() =>
        X >= 0 && X <= 1 && Y >= 0 && Y <= 1
        && Width > 0 && Height > 0
        && X + Width <= 1.0001 && Y + Height <= 1.0001;

    /// <summary>Prawy-dolny róg (czyli X+Width, Y+Height). Przydatne w walidacji.</summary>
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>Czy punkt (znormalizowany) leży wewnątrz tego prostokąta.</summary>
    public bool Contains(double nx, double ny) =>
        nx >= X && nx <= Right && ny >= Y && ny <= Bottom;
}
