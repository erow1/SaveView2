namespace SafeView.Domain.Cameras;

/// <summary>
/// Macierz homografii 3×3 mapująca punkty z płaszczyzny obrazu (znormalizowane [0..1])
/// na płaszczyznę podłogi (metry). Używana przez filtry przestrzenne w pipeline'ie.
///
/// Projekcja:  (x, y, 1)ᵀ × H  → (X', Y', W');  finalne: (X'/W', Y'/W').
///
/// Macierz jest policzana z minimum 4 par punktów (<see cref="HomographyCalculator"/>).
/// </summary>
public sealed class HomographyMatrix
{
    public double M00 { get; }
    public double M01 { get; }
    public double M02 { get; }
    public double M10 { get; }
    public double M11 { get; }
    public double M12 { get; }
    public double M20 { get; }
    public double M21 { get; }
    public double M22 { get; }

    public HomographyMatrix(
        double m00, double m01, double m02,
        double m10, double m11, double m12,
        double m20, double m21, double m22)
    {
        M00 = m00; M01 = m01; M02 = m02;
        M10 = m10; M11 = m11; M12 = m12;
        M20 = m20; M21 = m21; M22 = m22;
    }

    /// <summary>Rzutuje punkt obrazu (x, y) w [0..1] na podłogę (metry). Zwraca null gdy punkt degenerowany (W=0).</summary>
    public (double X, double Y)? Project(double pixelX, double pixelY)
    {
        var w = M20 * pixelX + M21 * pixelY + M22;
        if (Math.Abs(w) < 1e-12) return null;
        var x = (M00 * pixelX + M01 * pixelY + M02) / w;
        var y = (M10 * pixelX + M11 * pixelY + M12) / w;
        return (x, y);
    }
}
