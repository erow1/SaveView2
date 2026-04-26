using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Persistence;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Endpoint dla strony podglądu /monitor — zwraca geometrię (ROI + zones) oraz
/// ostatnie detekcje dla wybranej kamery. Koordynaty znormalizowane [0..1] kamery.
/// Strona rysuje overlay SVG nad strumieniem snapshot-ów.
///
/// Filtr klas: domyślnie endpoint zwraca TYLKO detekcje matchujące (model, label) triggerów
/// przypiętych do stref tej kamery — user widzi to co naprawdę go interesuje. Parametr
/// <c>?all=true</c> wyłącza filtr (debug — sprawdź co model w ogóle wykrywa).
/// </summary>
public static class MonitorEndpoints
{
    public static IEndpointRouteBuilder MapMonitorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/monitor/{cameraId}", async (
            string cameraId,
            bool? all,
            ICameraRepository cameras,
            IRoiRepository rois,
            IZoneRepository zones,
            ITriggerRepository triggers,
            IDetectionSnapshotStore snapshots,
            CancellationToken ct) =>
        {
            var camera = await cameras.GetByIdAsync(cameraId, ct);
            if (camera is null) return Results.NotFound();

            var cameraRois = await rois.ListEnabledByCameraAsync(cameraId, ct);
            var cameraZones = await zones.ListByCameraAsync(cameraId, ct);
            var snap = snapshots.GetLatest(cameraId);

            // Homografia — tylko gdy kamera ma min. 4 punkty kalibracji.
            var H = camera.CalibrationPoints.Count >= 4
                ? SafeView.Domain.Cameras.HomographyCalculator.Compute(camera.CalibrationPoints)
                : null;

            // Zbuduj allowlist (modelId → set(labels)) z triggerów przypiętych do stref kamery.
            // Pusty set labelów w condition = "dowolna klasa z tego modelu" → reprezentowane
            // jako null w wartości dictionary (oznacza wildcard).
            var triggerIds = cameraZones.SelectMany(z => z.TriggerIds).Distinct().ToList();
            var cameraTriggers = triggerIds.Count > 0
                ? (await triggers.ListByIdsAsync(triggerIds, ct)).ToList()
                : new List<SafeView.Domain.Detection.Trigger>();

            Dictionary<string, HashSet<string>?> allow = new(StringComparer.Ordinal);
            foreach (var t in cameraTriggers)
            {
                foreach (var c in t.Conditions)
                {
                    if (string.IsNullOrEmpty(c.ModelId)) continue;
                    if (c.Labels.Count == 0)
                    {
                        allow[c.ModelId] = null; // wildcard — wszystkie klasy z tego modelu
                    }
                    else if (allow.TryGetValue(c.ModelId, out var existing))
                    {
                        if (existing is not null)
                            foreach (var lab in c.Labels) existing.Add(lab);
                    }
                    else
                    {
                        allow[c.ModelId] = new HashSet<string>(c.Labels, StringComparer.Ordinal);
                    }
                }
            }

            bool showAll = all == true || allow.Count == 0; // brak triggerów = pokaż wszystko (bootstrap)

            var filteredDetections = (snap?.Detections ?? Array.Empty<DetectionResult>())
                .Where(d =>
                {
                    if (showAll) return true;
                    if (!allow.TryGetValue(d.ModelId, out var labels)) return false;
                    if (labels is null) return true; // wildcard dla tego modelu
                    return labels.Contains(d.Label);
                });

            return Results.Ok(new
            {
                rois = cameraRois.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    color = r.Color,
                    x = r.Rectangle.X,
                    y = r.Rectangle.Y,
                    width = r.Rectangle.Width,
                    height = r.Rectangle.Height
                }),
                zones = cameraZones
                    .Where(z => z.Enabled && z.Polygon.Count >= 3)
                    .Select(z => new
                    {
                        id = z.Id,
                        name = z.Name,
                        color = z.Color,
                        roiId = z.RoiId,
                        points = z.Polygon.Select(p => new { x = p.X, y = p.Y })
                    }),
                detections = filteredDetections.Select(d => new
                {
                    modelId = d.ModelId,
                    label = d.Label,
                    confidence = d.Confidence,
                    x = d.Bbox.X,
                    y = d.Bbox.Y,
                    width = d.Bbox.Width,
                    height = d.Bbox.Height
                }),
                capturedAt = snap?.CapturedAt,
                filtered = !showAll,
                totalDetections = snap?.Detections.Count ?? 0,
                homography = H is null ? null : new
                {
                    m00 = H.M00, m01 = H.M01, m02 = H.M02,
                    m10 = H.M10, m11 = H.M11, m12 = H.M12,
                    m20 = H.M20, m21 = H.M21, m22 = H.M22
                }
            });
        })
        .RequireAuthorization("perm:cameras:view")
        .WithTags("Monitor");

        return endpoints;
    }
}
