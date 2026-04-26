using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Domain.Detection;
using SafeView.Domain.Zones;

namespace SafeView.Application.Detection;

/// <summary>
/// Implementacja <see cref="IApiCameraProvisioner"/>.
///
/// Pełnokadrowa ROI: <c>Rectangle=(0,0,1,1)</c>, <c>IsFullFrame=true</c>, <c>InferenceMode=Native</c>,
/// <c>ModelIds=[]</c> (zewnętrzny system robi inferencję — nasze modele się nie odpalają).
///
/// Pełnokadrowa Zone: polygon = czworokąt obejmujący kadr (0,0)→(1,0)→(1,1)→(0,1).
///
/// Po pierwszym wywołaniu dla danej kamery oba dokumenty istnieją; ponowne wywołanie sprawdza
/// flagę <see cref="Roi.IsFullFrame"/> i pomija. Bez tego użytkownik musiałby ręcznie definiować
/// ROI/Strefę co byłoby bezsensowne dla push-based kamery.
/// </summary>
public sealed class ApiCameraProvisioner : IApiCameraProvisioner
{
    private readonly IRoiRepository _rois;
    private readonly IZoneRepository _zones;
    private readonly ILogger<ApiCameraProvisioner> _log;

    public ApiCameraProvisioner(IRoiRepository rois, IZoneRepository zones, ILogger<ApiCameraProvisioner> log)
    {
        _rois = rois;
        _zones = zones;
        _log = log;
    }

    public async Task EnsureFullFrameRoiAndZoneAsync(string cameraId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId))
            throw new ArgumentException("cameraId required", nameof(cameraId));

        // 1. ROI — szukaj pełnokadrowej; gdy brak, utwórz
        var existingRois = await _rois.ListEnabledByCameraAsync(cameraId, ct).ConfigureAwait(false);
        var fullRoi = existingRois.FirstOrDefault(r => r.IsFullFrame);
        if (fullRoi is null)
        {
            fullRoi = new Roi
            {
                CameraId = cameraId,
                Name = "Pełna klatka",
                Description = "Auto-provisioned dla kamery typu Api — provider decyduje co jest istotne.",
                Rectangle = new RoiRectangle { X = 0, Y = 0, Width = 1, Height = 1 },
                ModelIds = [], // zewnętrzny system robi inferencję
                InferenceMode = RoiInferenceMode.Native,
                Enabled = true,
                IsFullFrame = true,
                Color = "#4DA6FF"
            };
            await _rois.InsertAsync(fullRoi, ct).ConfigureAwait(false);
            _log.LogInformation("ApiCameraProvisioner: utworzono pełnokadrową ROI dla kamery {Camera}", cameraId);
        }

        // 2. Zone — szukaj pełnokadrowej (po RoiId fullRoi); gdy brak, utwórz
        var cameraZones = await _zones.ListByCameraAsync(cameraId, ct).ConfigureAwait(false);
        var hasFullZone = cameraZones.Any(z => z.RoiId == fullRoi.Id
                                                && z.Polygon.Count == 4
                                                && IsFullFramePolygon(z.Polygon));
        if (!hasFullZone)
        {
            var fullZone = new Zone
            {
                Name = "Pełna klatka",
                Description = "Auto-provisioned dla kamery typu Api — pokrywa cały kadr.",
                CameraId = cameraId,
                RoiId = fullRoi.Id,
                Polygon =
                [
                    new ZonePoint { X = 0, Y = 0 },
                    new ZonePoint { X = 1, Y = 0 },
                    new ZonePoint { X = 1, Y = 1 },
                    new ZonePoint { X = 0, Y = 1 }
                ],
                TriggerIds = [], // user dopina triggery przez normalny UI
                Enabled = true,
                Color = "#FF5964"
            };
            await _zones.InsertAsync(fullZone, ct).ConfigureAwait(false);
            _log.LogInformation("ApiCameraProvisioner: utworzono pełnokadrową strefę dla kamery {Camera}", cameraId);
        }
    }

    private static bool IsFullFramePolygon(List<ZonePoint> polygon)
    {
        if (polygon.Count != 4) return false;
        const double eps = 0.001;
        bool Has(double x, double y) =>
            polygon.Any(p => Math.Abs(p.X - x) < eps && Math.Abs(p.Y - y) < eps);
        return Has(0, 0) && Has(1, 0) && Has(1, 1) && Has(0, 1);
    }
}
