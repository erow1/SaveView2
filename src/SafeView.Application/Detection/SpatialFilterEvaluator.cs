using SafeView.Application.Abstractions.Detection;
using SafeView.Domain.Cameras;
using SafeView.Domain.Detection;

namespace SafeView.Application.Detection;

/// <summary>
/// Ocenia <see cref="SpatialFilter"/>-y na liście detekcji z klatki. Każdy filtr rzutuje
/// "stopę" bboxa (bottom-center) przez homografię kamery na podłogę, a potem sprawdza
/// warunek MIN/MAX dystansu między parami.
///
/// Filtry łączone są AND: wszystkie muszą przejść, żeby trigger wypalił.
/// </summary>
public static class SpatialFilterEvaluator
{
    /// <summary>Wynik ewaluacji — passed=true albo powód niepowodzenia.</summary>
    public sealed record Result(bool Passed, string? FailReason = null)
    {
        public static Result Ok() => new(true);
        public static Result Fail(string reason) => new(false, reason);
    }

    public static Result Evaluate(
        IReadOnlyList<SpatialFilter> filters,
        IReadOnlyList<DetectionResult> allDetections,
        HomographyMatrix? homography)
    {
        if (filters.Count == 0) return Result.Ok();
        if (homography is null)
            return Result.Fail("Kamera nie ma kalibracji homografii — filtry przestrzenne wymagają min. 4 punktów.");

        foreach (var f in filters)
        {
            var r = EvaluateOne(f, allDetections, homography);
            if (!r.Passed) return r;
        }
        return Result.Ok();
    }

    private static Result EvaluateOne(SpatialFilter f, IReadOnlyList<DetectionResult> all, HomographyMatrix H)
    {
        if (f.Kind != SpatialFilterKind.PairDistance)
            return Result.Fail($"Nieobsługiwany typ filtra: {f.Kind}");

        var groupA = all.Where(d => MatchesSide(d, f.ModelIdA, f.LabelsA)).ToList();
        var groupB = all.Where(d => MatchesSide(d, f.ModelIdB, f.LabelsB)).ToList();

        if (groupA.Count == 0 || groupB.Count == 0)
            return Result.Fail("Jedna ze stron filtra nie ma detekcji.");

        // Buduj pary — jeśli modelA == modelB, unikaj tej samej detekcji po obu stronach.
        bool selfPair = ReferenceEquals(groupA, groupB) || (f.ModelIdA == f.ModelIdB && AreSameLabels(f.LabelsA, f.LabelsB));

        int totalPairs = 0;
        int satisfyingPairs = 0;

        foreach (var a in groupA)
        {
            var fa = Foot(H, a);
            if (fa is null) continue;
            foreach (var b in groupB)
            {
                if (selfPair && ReferenceEquals(a, b)) continue;
                var fb = Foot(H, b);
                if (fb is null) continue;

                double dx = fa.Value.X - fb.Value.X;
                double dy = fa.Value.Y - fb.Value.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                totalPairs++;
                bool okMin = !f.MinDistanceM.HasValue || dist >= f.MinDistanceM.Value;
                bool okMax = !f.MaxDistanceM.HasValue || dist <= f.MaxDistanceM.Value;
                if (okMin && okMax) satisfyingPairs++;
            }
        }

        if (totalPairs == 0)
            return Result.Fail("Brak sensownych par (detekcje poza zasięgiem homografii).");

        return f.PairMode switch
        {
            SpatialPairMode.AnyPair => satisfyingPairs > 0
                ? Result.Ok()
                : Result.Fail($"Żadna para nie mieści się w {FormatRange(f)}."),
            SpatialPairMode.AllPairs => satisfyingPairs == totalPairs
                ? Result.Ok()
                : Result.Fail($"{totalPairs - satisfyingPairs} / {totalPairs} par poza {FormatRange(f)}."),
            _ => Result.Fail("Nieznany tryb pair mode.")
        };
    }

    private static bool MatchesSide(DetectionResult d, string modelId, List<string> labels)
    {
        if (d.ModelId != modelId) return false;
        if (labels.Count == 0) return true;
        return labels.Contains(d.Label);
    }

    private static bool AreSameLabels(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        var setA = new HashSet<string>(a, StringComparer.Ordinal);
        return b.All(x => setA.Contains(x));
    }

    /// <summary>
    /// "Stopa" bboxa w [0..1] → rzut przez homografię na metry.
    /// Dla detekcji na podłodze bottom-center to najlepszy proxy pozycji fizycznej.
    /// </summary>
    private static (double X, double Y)? Foot(HomographyMatrix H, DetectionResult d)
    {
        double px = d.Bbox.X + d.Bbox.Width / 2.0;
        double py = d.Bbox.Y + d.Bbox.Height;
        return H.Project(px, py);
    }

    private static string FormatRange(SpatialFilter f)
    {
        var min = f.MinDistanceM.HasValue ? $"{f.MinDistanceM.Value:F1} m" : "–";
        var max = f.MaxDistanceM.HasValue ? $"{f.MaxDistanceM.Value:F1} m" : "–";
        return $"[{min} … {max}]";
    }
}
