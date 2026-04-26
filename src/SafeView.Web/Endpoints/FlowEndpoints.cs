using SafeView.Application.Abstractions.Persistence;

namespace SafeView.Web.Endpoints;

/// <summary>
/// Topologia pipeline'u detekcji jako graf — node'y: kamery, ROI, strefy, triggery, akcje, modele;
/// edge'y: relacje z DB (Roi.CameraId, Zone.RoiId, Zone.TriggerIds, Trigger.ActionIds, Roi.ModelIds).
/// Konsumowane przez stronę /flow (Cytoscape.js). Tylko odczyt — edycja przez osobne CRUD-y.
/// </summary>
public static class FlowEndpoints
{
    public static IEndpointRouteBuilder MapFlowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/flow/topology", async (
            ICameraRepository cameras,
            IRoiRepository rois,
            IZoneRepository zones,
            ITriggerRepository triggers,
            IActionRepository actions,
            CancellationToken ct) =>
        {
            var allCameras = await cameras.ListAsync(ct);
            var allRois = await rois.ListAsync(ct);
            var allZones = await zones.ListAsync(ct);
            var allTriggers = await triggers.ListAsync(ct);
            var allActions = await actions.ListAsync(ct);

            var nodes = new List<object>();
            var edges = new List<object>();

            foreach (var c in allCameras)
                nodes.Add(new { id = $"cam:{c.Id}", type = "camera", label = c.Name, enabled = c.Enabled });

            foreach (var r in allRois)
            {
                nodes.Add(new { id = $"roi:{r.Id}", type = "roi", label = r.Name, enabled = r.Enabled, color = r.Color });
                edges.Add(new { source = $"cam:{r.CameraId}", target = $"roi:{r.Id}", kind = "camera-roi" });
            }

            foreach (var z in allZones.Where(z => z.Enabled))
            {
                nodes.Add(new { id = $"zon:{z.Id}", type = "zone", label = z.Name, enabled = z.Enabled, color = z.Color });
                if (!string.IsNullOrEmpty(z.RoiId))
                    edges.Add(new { source = $"roi:{z.RoiId}", target = $"zon:{z.Id}", kind = "roi-zone" });
                foreach (var tid in z.TriggerIds)
                    edges.Add(new { source = $"zon:{z.Id}", target = $"trg:{tid}", kind = "zone-trigger" });
            }

            foreach (var t in allTriggers)
            {
                nodes.Add(new { id = $"trg:{t.Id}", type = "trigger", label = t.Name, enabled = t.Enabled });
                foreach (var aid in t.ActionIds)
                    edges.Add(new { source = $"trg:{t.Id}", target = $"act:{aid}", kind = "trigger-action" });
            }

            foreach (var a in allActions)
                nodes.Add(new { id = $"act:{a.Id}", type = "action", label = a.Name, enabled = a.Enabled, actionType = a.Type.ToString() });

            return Results.Ok(new { nodes, edges });
        })
        .RequireAuthorization("perm:cameras:view")
        .WithTags("Flow");

        return endpoints;
    }
}
