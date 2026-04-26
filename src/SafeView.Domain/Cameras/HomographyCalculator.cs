namespace SafeView.Domain.Cameras;

/// <summary>
/// Oblicza macierz homografii 3×3 z par punktów (obraz ↔ podłoga).
/// Algorytm: Direct Linear Transform (DLT) z normalizacją h22=1. Dla N=4 rozwiązanie
/// jest dokładne, dla N&gt;4 — least-squares przez normalne równania (AᵀA·h = Aᵀb).
/// Solver: eliminacja Gaussa z partial pivoting, wystarczająco stabilna dla N≤20 punktów.
/// </summary>
public static class HomographyCalculator
{
    /// <summary>
    /// Liczy macierz lub zwraca null gdy:
    ///  • punktów jest &lt; 4
    ///  • punkty są zdegenerowane (koliniarne, duplikaty) i układ jest osobliwy.
    /// Walidacja sensowności macierzy po stronie klienta (np. odrzuć gdy projekcje sensownych
    /// punktów dają wyniki absurdalne).
    /// </summary>
    public static HomographyMatrix? Compute(IReadOnlyList<CalibrationPoint> points)
    {
        if (points.Count < 4) return null;

        // Budujemy A (2N × 8) i b (2N) — każdy punkt 2 równania.
        int n = points.Count;
        var A = new double[2 * n, 8];
        var b = new double[2 * n];

        for (int i = 0; i < n; i++)
        {
            var p = points[i];
            double x = p.PixelX, y = p.PixelY;
            double X = p.WorldX, Y = p.WorldY;

            // wiersz 2i: X-row
            A[2 * i, 0] = x; A[2 * i, 1] = y; A[2 * i, 2] = 1;
            A[2 * i, 3] = 0; A[2 * i, 4] = 0; A[2 * i, 5] = 0;
            A[2 * i, 6] = -X * x; A[2 * i, 7] = -X * y;
            b[2 * i] = X;

            // wiersz 2i+1: Y-row
            A[2 * i + 1, 0] = 0; A[2 * i + 1, 1] = 0; A[2 * i + 1, 2] = 0;
            A[2 * i + 1, 3] = x; A[2 * i + 1, 4] = y; A[2 * i + 1, 5] = 1;
            A[2 * i + 1, 6] = -Y * x; A[2 * i + 1, 7] = -Y * y;
            b[2 * i + 1] = Y;
        }

        // Normalne równania: M = AᵀA (8×8), c = Aᵀb (8)
        var M = new double[8, 8];
        var c = new double[8];
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                double sum = 0;
                for (int k = 0; k < 2 * n; k++) sum += A[k, i] * A[k, j];
                M[i, j] = sum;
            }
            double cs = 0;
            for (int k = 0; k < 2 * n; k++) cs += A[k, i] * b[k];
            c[i] = cs;
        }

        if (!SolveGauss(M, c, out var h)) return null;

        return new HomographyMatrix(
            h[0], h[1], h[2],
            h[3], h[4], h[5],
            h[6], h[7], 1.0);
    }

    /// <summary>Eliminacja Gaussa z partial pivoting. Zwraca false gdy układ osobliwy.</summary>
    private static bool SolveGauss(double[,] M, double[] c, out double[] result)
    {
        int n = c.Length;
        var a = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) a[i, j] = M[i, j];
            a[i, n] = c[i];
        }

        for (int i = 0; i < n; i++)
        {
            // partial pivot — znajdź wiersz z max |a[k,i]| dla k>=i
            int pivot = i;
            double maxAbs = Math.Abs(a[i, i]);
            for (int k = i + 1; k < n; k++)
            {
                var v = Math.Abs(a[k, i]);
                if (v > maxAbs) { maxAbs = v; pivot = k; }
            }
            if (maxAbs < 1e-12) { result = Array.Empty<double>(); return false; }
            if (pivot != i)
            {
                for (int j = 0; j <= n; j++)
                    (a[i, j], a[pivot, j]) = (a[pivot, j], a[i, j]);
            }
            // eliminacja
            for (int k = i + 1; k < n; k++)
            {
                var factor = a[k, i] / a[i, i];
                for (int j = i; j <= n; j++) a[k, j] -= factor * a[i, j];
            }
        }

        // back-substitution
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            var sum = a[i, n];
            for (int j = i + 1; j < n; j++) sum -= a[i, j] * x[j];
            x[i] = sum / a[i, i];
        }

        result = x;
        return true;
    }
}
