using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SafeView.Application.Abstractions.Detection;
using SafeView.Application.Abstractions.Diagnostics;
using SafeView.Application.Abstractions.ML;
using SafeView.Application.Abstractions.Media;
using SafeView.Application.Abstractions.Persistence;
using SafeView.Application.Abstractions.Time;
using SafeView.Domain.Cameras;
using SafeView.Domain.Detection;
using SafeView.Domain.Detection.Geometry;
using SafeView.Domain.Zones;
using MlDetectionResult = SafeView.Application.Abstractions.ML.DetectionResult;
using MlDetection = SafeView.Application.Abstractions.ML.Detection;
using DetectionResult = SafeView.Application.Abstractions.Detection.DetectionResult;

namespace SafeView.Application.Detection;

/// <summary>
/// DetectionPipeline (Faza 2) — główny orchestrator detekcji z optymalizacją per-ROI.
///
/// Faza 2 rozszerzenia vs Fazy 1:
///  • Crop ROI z klatki (nie wysyłamy całej 4K klatki do modelu — tylko istotny fragment)
///  • Wybór strategii inferencji per ROI: Native / Resize / Sliced / Adaptive
///  • Sliced Inference (SAHI) — tiling ROI na fragmenty ~640×640, sekwencyjna inferencja + NMS merge
///  • Performance logging — czas per-ROI do logu (Debug+Warning) → system_events
///
/// Flow per-klatka:
///   1. Pobierz enabled ROI kamery + strefy + triggery + akcje (dedup ID → 1 query per type)
///   2. Dla każdego ROI:
///      a. Crop klatki do prostokąta ROI → tempfile (crop w natywnej rozdzielczości)
///      b. Dla każdego model w ROI.ModelIds:
///         • Wybierz detector (factory z InferenceMode)
///         • Uruchom detekcję na cropie
///         • Przetransformuj bboxy z układu cropa do układu kamery (offset + normalize)
///      c. Usuń tempfile
///   3. Dla każdej Zone × Trigger → filter detekcji, evaluate, dispatch actions
/// </summary>
public sealed class DetectionPipeline : IDetectionPipeline, IFrameObserver
{
    private readonly IRoiRepository _rois;
    private readonly IZoneRepository _zones;
    private readonly ITriggerRepository _triggers;
    private readonly IActionRepository _actions;
    private readonly IMLModelRepository _models;
    private readonly IDetectionClassRepository? _detectionClasses;
    private readonly IDetectorFactory _detectorFactory;
    private readonly IRoiCropper _cropper;
    private readonly ITriggerEvaluator _triggerEvaluator;
    private readonly IActionDispatcher _dispatcher;
    private readonly IActionExecutionRepository _auditLog;
    private readonly IIncidentRepository? _incidents;
    private readonly IVllmChecker? _vllmChecker;
    private readonly IPerformanceMetrics? _metrics;
    private readonly IDetectionSnapshotStore? _snapshots;
    private readonly IFlowEventPublisher? _flow;
    private readonly IClock _clock;
    private readonly ILogger<DetectionPipeline> _log;

    public DetectionPipeline(
        IRoiRepository rois,
        IZoneRepository zones,
        ITriggerRepository triggers,
        IActionRepository actions,
        IMLModelRepository models,
        IDetectorFactory detectorFactory,
        IRoiCropper cropper,
        ITriggerEvaluator triggerEvaluator,
        IActionDispatcher dispatcher,
        IActionExecutionRepository auditLog,
        IClock clock,
        ILogger<DetectionPipeline> log,
        IVllmChecker? vllmChecker = null,
        IPerformanceMetrics? metrics = null,
        IDetectionSnapshotStore? snapshots = null,
        IIncidentRepository? incidents = null,
        IFlowEventPublisher? flow = null,
        IDetectionClassRepository? detectionClasses = null)
    {
        _rois = rois;
        _zones = zones;
        _triggers = triggers;
        _actions = actions;
        _models = models;
        _detectorFactory = detectorFactory;
        _cropper = cropper;
        _triggerEvaluator = triggerEvaluator;
        _dispatcher = dispatcher;
        _auditLog = auditLog;
        _clock = clock;
        _log = log;
        _vllmChecker = vllmChecker;
        _metrics = metrics; // optional — pipeline działa bez instrumentacji gdy null
        _snapshots = snapshots;
        _incidents = incidents;
        _flow = flow;
        _detectionClasses = detectionClasses; // optional — null = tylko legacy path
    }

    public Task OnFrameAsync(Camera camera, SnapshotResult frame, CancellationToken ct)
    {
        if (!frame.Success || string.IsNullOrEmpty(frame.AbsolutePath)) return Task.CompletedTask;
        return ProcessFrameAsync(camera, frame.AbsolutePath, frame.RelativePath, ct);
    }

    public Task ProcessFrameAsync(Camera camera, string framePath, CancellationToken ct = default)
        => ProcessFrameAsync(camera, framePath, null, ct);

    public async Task ProcessFrameAsync(Camera camera, string framePath, string? frameRelativePath, CancellationToken ct = default)
    {
        var pipelineSw = Stopwatch.StartNew();
        try
        {
            // 1. Pobierz enabled ROI dla kamery
            var rois = await _rois.ListEnabledByCameraAsync(camera.Id, ct).ConfigureAwait(false);
            if (rois.Count == 0) return;

            // 2. Pobierz strefy (tylko w nowym schemacie: z RoiId + z triggerami)
            var cameraZones = (await _zones.ListByCameraAsync(camera.Id, ct).ConfigureAwait(false))
                .Where(z => z.Enabled
                            && z.Polygon.Count >= 3
                            && !string.IsNullOrWhiteSpace(z.RoiId)
                            && z.TriggerIds.Count > 0)
                .ToList();
            if (cameraZones.Count == 0) return;

            // 3. Detekcja per ROI — cropuje klatkę do ROI, uruchamia modele, transformuje koordynaty
            var allDetections = new List<DetectionResult>();
            foreach (var roi in rois)
            {
                if (roi.ModelIds.Count == 0) continue;
                ct.ThrowIfCancellationRequested();

                var roiDetections = await RunModelsForRoiAsync(roi, framePath, camera.Name, ct)
                    .ConfigureAwait(false);
                allDetections.AddRange(roiDetections);
            }

            // 4-5. Wspólna ścieżka post-detection: snapshot store, batch repo loads, eval, VLLM, dispatch.
            await EvaluateAndDispatchAsync(camera, rois, cameraZones, allDetections,
                framePath, frameRelativePath, _clock.UtcNow, ct).ConfigureAwait(false);

            pipelineSw.Stop();

            _metrics?.Record(new PerformanceSample(
                Timestamp: _clock.UtcNow,
                Stage: "pipeline",
                CameraId: camera.Id,
                RoiId: null,
                ModelId: null,
                DurationMs: (int)pipelineSw.ElapsedMilliseconds,
                DetectionCount: allDetections.Count,
                Success: true));

            if (pipelineSw.ElapsedMilliseconds > 500)
            {
                _log.LogWarning(
                    "DetectionPipeline: klatka {Camera} zajęła {Ms}ms ({Rois} ROI, {Detections} detekcji). " +
                    "Rozważ tryb Sliced dla dużych ROI lub mniejszy model.",
                    camera.Name, pipelineSw.ElapsedMilliseconds, rois.Count, allDetections.Count);
            }
            else
            {
                _log.LogDebug("DetectionPipeline: {Camera} {Ms}ms, {Rois} ROI, {Det} det.",
                    camera.Name, pipelineSw.ElapsedMilliseconds, rois.Count, allDetections.Count);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            pipelineSw.Stop();
            _metrics?.Record(new PerformanceSample(
                Timestamp: _clock.UtcNow,
                Stage: "pipeline",
                CameraId: camera.Id,
                RoiId: null, ModelId: null,
                DurationMs: (int)pipelineSw.ElapsedMilliseconds,
                DetectionCount: 0,
                Success: false,
                ErrorType: ex.GetType().Name));
            _log.LogError(ex, "DetectionPipeline failed for camera {Camera}", camera.Name);
        }
    }

    /// <summary>
    /// Push-based wariant — kamery typu <see cref="CameraTransport.Api"/> dostarczają detekcje
    /// z zewnątrz (zewnętrzny ML inference). Pomija stage inferencji i wpada od razu do shared
    /// post-detection path. Downstream nie wie że źródło jest zewnętrzne.
    /// </summary>
    public async Task ProcessExternalDetectionsAsync(
        Camera camera,
        string framePath,
        string? frameRelativePath,
        IReadOnlyList<DetectionResult> externalDetections,
        DateTime capturedAt,
        CancellationToken ct = default)
    {
        var pipelineSw = Stopwatch.StartNew();
        try
        {
            var rois = await _rois.ListEnabledByCameraAsync(camera.Id, ct).ConfigureAwait(false);
            if (rois.Count == 0)
            {
                _log.LogDebug("ApiCamera ingest: kamera '{Camera}' nie ma ROI — pomijam", camera.Name);
                return;
            }

            var cameraZones = (await _zones.ListByCameraAsync(camera.Id, ct).ConfigureAwait(false))
                .Where(z => z.Enabled
                            && z.Polygon.Count >= 3
                            && !string.IsNullOrWhiteSpace(z.RoiId)
                            && z.TriggerIds.Count > 0)
                .ToList();
            if (cameraZones.Count == 0)
            {
                _log.LogDebug("ApiCamera ingest: kamera '{Camera}' nie ma stref z triggerami — pomijam", camera.Name);
                return;
            }

            await EvaluateAndDispatchAsync(camera, rois, cameraZones, externalDetections,
                framePath, frameRelativePath, capturedAt, ct).ConfigureAwait(false);

            pipelineSw.Stop();
            _metrics?.Record(new PerformanceSample(
                Timestamp: _clock.UtcNow,
                Stage: "pipeline_external",
                CameraId: camera.Id,
                RoiId: null,
                ModelId: null,
                DurationMs: (int)pipelineSw.ElapsedMilliseconds,
                DetectionCount: externalDetections.Count,
                Success: true));

            _log.LogDebug("ApiCamera ingest: {Camera} {Ms}ms, {Det} ext-detections",
                camera.Name, pipelineSw.ElapsedMilliseconds, externalDetections.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            pipelineSw.Stop();
            _metrics?.Record(new PerformanceSample(
                Timestamp: _clock.UtcNow,
                Stage: "pipeline_external",
                CameraId: camera.Id,
                RoiId: null, ModelId: null,
                DurationMs: (int)pipelineSw.ElapsedMilliseconds,
                DetectionCount: 0,
                Success: false,
                ErrorType: ex.GetType().Name));
            _log.LogError(ex, "DetectionPipeline (external) failed for camera {Camera}", camera.Name);
        }
    }

    /// <summary>
    /// Wspólna ścieżka post-detection — używana przez normalny flow (ProcessFrameAsync) i push-based
    /// (ProcessExternalDetectionsAsync). Nie wie skąd pochodzą detekcje, robi te same rzeczy:
    /// snapshot store update, batch load triggers/actions/classes, homography compute, eval+VLLM+dispatch.
    /// </summary>
    private async Task EvaluateAndDispatchAsync(
        Camera camera,
        IReadOnlyList<SafeView.Domain.Detection.Roi> rois,
        IReadOnlyList<Zone> cameraZones,
        IReadOnlyList<DetectionResult> allDetections,
        string framePath,
        string? frameRelativePath,
        DateTime occurredAt,
        CancellationToken ct)
    {
        // Publikuj snapshot do podglądu /monitor (także gdy empty — user widzi że pipeline żyje).
        // Zapisujemy ścieżkę klatki, żeby Monitor mógł pokazać obraz z TEGO momentu detekcji
        // (bboxy się zgadzają z obrazem — bez "przesunięcia" obiektu względem ramki).
        _snapshots?.Update(new DetectionSnapshot(camera.Id, occurredAt, allDetections, frameRelativePath));

        if (allDetections.Count == 0) return;

        // Pobierz wszystkie triggery + akcje z jednym query per type
        var allTriggerIds = cameraZones.SelectMany(z => z.TriggerIds).Distinct().ToList();
        var triggers = (await _triggers.ListByIdsAsync(allTriggerIds, ct).ConfigureAwait(false))
            .ToDictionary(t => t.Id);

        var allActionIds = triggers.Values.SelectMany(t => t.ActionIds).Distinct().ToList();
        var actions = (await _actions.ListByIdsAsync(allActionIds, ct).ConfigureAwait(false))
            .ToDictionary(a => a.Id);

        // Rezolucja DetectionClass — jedno batch query dla wszystkich unique class IDs.
        IReadOnlyDictionary<string, SafeView.Domain.Detection.DetectionClass>? detectionClasses = null;
        if (_detectionClasses is not null)
        {
            var classIds = triggers.Values
                .SelectMany(t => t.Conditions)
                .Select(c => c.DetectionClassId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .Distinct()
                .ToList();
            if (classIds.Count > 0)
            {
                var classes = await _detectionClasses.ListByIdsAsync(classIds, ct).ConfigureAwait(false);
                detectionClasses = classes.ToDictionary(c => c.Id);
            }
        }

        // Homografia kamery — liczona raz per-klatka. Null gdy kalibracja brak / niepoprawna.
        // Dla kamer Api zwykle brak — spatial filters wtedy blokują (bezpieczniej).
        var homography = camera.CalibrationPoints.Count >= 4
            ? SafeView.Domain.Cameras.HomographyCalculator.Compute(camera.CalibrationPoints)
            : null;

        // Dla każdej Zone × Trigger — ewaluuj, dispatcher akcji
        foreach (var zone in cameraZones)
        {
            foreach (var triggerId in zone.TriggerIds)
            {
                if (!triggers.TryGetValue(triggerId, out var trigger)) continue;

                var matchingDetections = FilterDetectionsForZone(allDetections, trigger, zone, detectionClasses);
                var result = _triggerEvaluator.Evaluate(trigger, zone.Id, matchingDetections, detectionClasses);
                if (!result.Fired) continue;

                if (trigger.SpatialFilters.Count > 0)
                {
                    var spatial = SpatialFilterEvaluator.Evaluate(trigger.SpatialFilters, allDetections, homography);
                    if (!spatial.Passed)
                    {
                        _log.LogDebug("Spatial filter blocked trigger '{Trigger}' in zone '{Zone}': {Reason}",
                            trigger.Name, zone.Name, spatial.FailReason);
                        continue;
                    }
                }

                var triggerActions = trigger.ActionIds
                    .Select(id => actions.GetValueOrDefault(id))
                    .Where(a => a is not null)
                    .Cast<DetectionAction>()
                    .ToList();

                if (triggerActions.Count == 0) continue;

                var context = new ActionContext
                {
                    CameraId = camera.Id,
                    CameraName = camera.Name,
                    ZoneId = zone.Id,
                    ZoneName = zone.Name,
                    TriggerId = trigger.Id,
                    TriggerName = trigger.Name,
                    FrameSnapshotPath = framePath,
                    Detections = matchingDetections,
                    OccurredAt = occurredAt
                };

                VllmCheckResult? vllmResult = null;
                if (trigger.VllmCheck is { Enabled: true } vllmCfg && _vllmChecker is not null)
                {
                    vllmResult = await _vllmChecker.CheckAsync(vllmCfg, context, ct).ConfigureAwait(false);

                    if (vllmResult.IsError)
                    {
                        if (vllmCfg.RejectOnError)
                        {
                            _log.LogWarning("VLLM error for trigger '{Trigger}' + RejectOnError=true → skipping actions. Reason: {Reason}",
                                trigger.Name, vllmResult.Reason);
                            await LogVllmSkipAsync(triggerActions, context, ActionSkipReason.VllmRejected,
                                $"VLLM error: {vllmResult.Reason}", ct).ConfigureAwait(false);
                            continue;
                        }
                        _log.LogWarning("VLLM error for trigger '{Trigger}' + RejectOnError=false → firing actions anyway. Reason: {Reason}",
                            trigger.Name, vllmResult.Reason);
                    }
                    else if (!vllmResult.Confirmed)
                    {
                        _log.LogInformation("VLLM rejected trigger '{Trigger}' in zone '{Zone}' (conf={Conf:F2}): {Reason}",
                            trigger.Name, zone.Name, vllmResult.Confidence, vllmResult.Reason);
                        await LogVllmSkipAsync(triggerActions, context, ActionSkipReason.VllmRejected,
                            $"VLLM: {vllmResult.Reason} (conf={vllmResult.Confidence:F2})", ct).ConfigureAwait(false);
                        if (_flow is not null) await _flow.VllmRejectedAsync(trigger.Id, zone.Id, camera.Id, ct).ConfigureAwait(false);
                        continue;
                    }
                    else
                    {
                        _log.LogDebug("VLLM confirmed trigger '{Trigger}' (conf={Conf:F2}): {Reason}",
                            trigger.Name, vllmResult.Confidence, vllmResult.Reason);
                    }
                }

                await CreateIncidentAsync(camera, zone, trigger, matchingDetections,
                    rois, framePath, frameRelativePath, vllmResult, detectionClasses, occurredAt, ct).ConfigureAwait(false);

                if (_flow is not null)
                    await _flow.TriggerFiredAsync(trigger.Id, zone.Id, camera.Id, matchingDetections.Count, ct).ConfigureAwait(false);

                await _dispatcher.DispatchAsync(triggerActions, context, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Uruchamia modele dla ROI. Obsługuje dwa tryby:
    ///  • Normal (default) — każdy model z <c>ROI.ModelIds</c> działa na pełnym cropie ROI
    ///  • Cascade (Faza 5) — gdy <c>ROI.ProposerModelId</c> jest ustawione:
    ///     1. Proposer (szybki, np. yolov8n) działa na ROI → zwraca kandydatów
    ///     2. Każdy kandydat (powyżej threshold) → wycięty z paddingiem → confirmerzy z ModelIds
    ///
    /// Oszczędza zasoby gdy zdarzenia są rzadkie — ciężki model odpala się tylko na kandydatach.
    /// </summary>
    private async Task<List<DetectionResult>> RunModelsForRoiAsync(
        SafeView.Domain.Detection.Roi roi, string framePath, string cameraName, CancellationToken ct)
    {
        // Wybór flow: cascade czy normal
        if (!string.IsNullOrEmpty(roi.ProposerModelId))
            return await RunCascadeForRoiAsync(roi, framePath, cameraName, ct).ConfigureAwait(false);

        return await RunNormalForRoiAsync(roi, framePath, cameraName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Normalny (bez cascade) flow: cropuj ROI → uruchom każdy z ModelIds.
    /// </summary>
    private async Task<List<DetectionResult>> RunNormalForRoiAsync(
        SafeView.Domain.Detection.Roi roi, string framePath, string cameraName, CancellationToken ct)
    {
        var results = new List<DetectionResult>();

        // Wytnij ROI → tempfile. Jeden crop dla wszystkich modeli tego ROI.
        RoiCropResult? crop = null;
        try
        {
            crop = await _cropper.CropAsync(framePath, roi.Rectangle, ct).ConfigureAwait(false);

            foreach (var modelId in roi.ModelIds)
            {
                ct.ThrowIfCancellationRequested();
                var model = await _models.GetByIdAsync(modelId, ct).ConfigureAwait(false);
                if (model is null || !model.Enabled) continue;

                // Wybierz tryb: Adaptive → heurystyka na rozmiarze cropa vs inputSize modelu
                var effectiveMode = roi.InferenceMode == RoiInferenceMode.Adaptive
                    ? TileGrid.PickAdaptiveMode(crop.CropWidth, crop.CropHeight, model.InputSize)
                    : roi.InferenceMode;

                var detector = _detectorFactory.GetFor(model, effectiveMode);

                var sw = Stopwatch.StartNew();
                MlDetectionResult detResult;
                try
                {
                    detResult = await detector.DetectAsync(model, crop.CropPath, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Detection failed for model {Model} ROI {Roi} on camera {Camera}",
                        model.Name, roi.Name, cameraName);
                    continue;
                }
                sw.Stop();

                if (!detResult.Success)
                {
                    _log.LogDebug("Detection unsuccessful: {Err}", detResult.ErrorMessage);
                    continue;
                }

                _log.LogDebug(
                    "ROI '{Roi}' × model '{Model}' ({Mode}): {Ms}ms → {Det} detections",
                    roi.Name, model.Name, effectiveMode, sw.ElapsedMilliseconds, detResult.Detections.Count);

                _metrics?.Record(new PerformanceSample(
                    Timestamp: _clock.UtcNow,
                    Stage: "roi_inference",
                    CameraId: null, // nie wiemy tu o kamerze; pipeline-level ma cameraId
                    RoiId: roi.Id,
                    ModelId: modelId,
                    DurationMs: (int)sw.ElapsedMilliseconds,
                    DetectionCount: detResult.Detections.Count,
                    Success: detResult.Success));

                // Transformacja: bbox jest w pikselach cropa → przesuwamy o offset cropa → normalizujemy
                // do [0..1] względem oryginalnej klatki kamery.
                var w = crop.OriginalWidth > 0 ? crop.OriginalWidth : 1;
                var h = crop.OriginalHeight > 0 ? crop.OriginalHeight : 1;
                foreach (var d in detResult.Detections)
                {
                    var absX = d.Box.X + crop.OffsetX;
                    var absY = d.Box.Y + crop.OffsetY;

                    results.Add(new DetectionResult(
                        modelId,
                        d.Label,
                        d.Confidence,
                        new Bbox(absX / w, absY / h, d.Box.Width / w, d.Box.Height / h)));
                }
            }
        }
        finally
        {
            if (crop is not null) _cropper.Cleanup(crop);
        }

        return results;
    }

    /// <summary>
    /// Cascade flow (Faza 5):
    ///  1. Proposer model (lekki) szybko znajduje kandydatów na ROI crop
    ///  2. Dla każdego kandydata >= threshold → wytnij bbox z paddingiem → uruchom confirmerów z ModelIds
    ///  3. Zwróć detekcje confirmerów (transformowane do układu kamery)
    ///
    /// Jeśli proposer nic nie znalazł — confirmerzy NIE są uruchamiani (kluczowa oszczędność).
    /// </summary>
    private async Task<List<DetectionResult>> RunCascadeForRoiAsync(
        SafeView.Domain.Detection.Roi roi, string framePath, string cameraName, CancellationToken ct)
    {
        var results = new List<DetectionResult>();

        // 1. Pobierz proposer model
        var proposer = await _models.GetByIdAsync(roi.ProposerModelId!, ct).ConfigureAwait(false);
        if (proposer is null || !proposer.Enabled)
        {
            _log.LogWarning("Cascade ROI '{Roi}': proposer model {Id} not found or disabled — skipping",
                roi.Name, roi.ProposerModelId);
            return results;
        }

        // 2. Crop ROI i uruchom proposera
        RoiCropResult? roiCrop = null;
        try
        {
            roiCrop = await _cropper.CropAsync(framePath, roi.Rectangle, ct).ConfigureAwait(false);

            var effectiveMode = roi.InferenceMode == RoiInferenceMode.Adaptive
                ? TileGrid.PickAdaptiveMode(roiCrop.CropWidth, roiCrop.CropHeight, proposer.InputSize)
                : roi.InferenceMode;
            var proposerDetector = _detectorFactory.GetFor(proposer, effectiveMode);

            var swProposer = Stopwatch.StartNew();
            MlDetectionResult proposerResult;
            try
            {
                proposerResult = await proposerDetector.DetectAsync(proposer, roiCrop.CropPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cascade proposer '{Model}' failed on ROI '{Roi}' camera '{Camera}'",
                    proposer.Name, roi.Name, cameraName);
                return results;
            }
            swProposer.Stop();

            if (!proposerResult.Success || proposerResult.Detections.Count == 0)
            {
                _log.LogDebug("Cascade ROI '{Roi}': proposer found 0 candidates ({Ms}ms) — skipping confirmers",
                    roi.Name, swProposer.ElapsedMilliseconds);
                return results;
            }

            // 3. Filtruj kandydatów po threshold
            var threshold = roi.ProposerConfidenceThreshold;
            var candidates = proposerResult.Detections
                .Where(d => d.Confidence >= threshold)
                .ToList();

            _log.LogDebug("Cascade ROI '{Roi}': proposer '{Model}' {Ms}ms → {Total} detections, {Pass} above threshold {Thr:F2}",
                roi.Name, proposer.Name, swProposer.ElapsedMilliseconds,
                proposerResult.Detections.Count, candidates.Count, threshold);

            _metrics?.Record(new PerformanceSample(
                Timestamp: _clock.UtcNow,
                Stage: "proposer",
                CameraId: null,
                RoiId: roi.Id,
                ModelId: proposer.Id,
                DurationMs: (int)swProposer.ElapsedMilliseconds,
                DetectionCount: proposerResult.Detections.Count,
                Success: proposerResult.Success));

            if (candidates.Count == 0) return results;

            // 4. Pobierz confirmerów (enabled)
            var confirmers = new List<SafeView.Domain.ML.MLModel>();
            foreach (var mid in roi.ModelIds)
            {
                var m = await _models.GetByIdAsync(mid, ct).ConfigureAwait(false);
                if (m is not null && m.Enabled) confirmers.Add(m);
            }
            if (confirmers.Count == 0)
            {
                _log.LogDebug("Cascade ROI '{Roi}': no enabled confirmers — returning proposer detections directly", roi.Name);
                // Fallback — jeśli nie ma confirmerów, traktuj proposer jak jedyny detektor
                return TransformDetectionsToCamera(candidates.Select(d => (proposer.Id, d)),
                    roiCrop.OffsetX, roiCrop.OffsetY, roiCrop.OriginalWidth, roiCrop.OriginalHeight);
            }

            // 5. Dla każdego kandydata: wytnij bbox (w pikselach oryginalnej klatki) z paddingiem → confirmerzy
            foreach (var cand in candidates)
            {
                ct.ThrowIfCancellationRequested();

                // Koords kandydata są w pikselach ROI crop → przesuwamy o offset ROI → globalne piksele klatki
                var bboxX = (int)Math.Round(cand.Box.X + roiCrop.OffsetX);
                var bboxY = (int)Math.Round(cand.Box.Y + roiCrop.OffsetY);
                var bboxW = (int)Math.Round(cand.Box.Width);
                var bboxH = (int)Math.Round(cand.Box.Height);

                RoiCropResult? bboxCrop = null;
                try
                {
                    bboxCrop = await _cropper.CropBboxAsync(
                        framePath, bboxX, bboxY, bboxW, bboxH,
                        paddingRatio: roi.CascadeBboxPadding,
                        minSize: 128,
                        ct).ConfigureAwait(false);

                    foreach (var confirmer in confirmers)
                    {
                        ct.ThrowIfCancellationRequested();
                        var confirmerMode = roi.InferenceMode == RoiInferenceMode.Adaptive
                            ? TileGrid.PickAdaptiveMode(bboxCrop.CropWidth, bboxCrop.CropHeight, confirmer.InputSize)
                            : RoiInferenceMode.Native; // bbox crop jest zwykle mały — native wystarczy
                        var confirmerDetector = _detectorFactory.GetFor(confirmer, confirmerMode);

                        MlDetectionResult confRes;
                        try
                        {
                            confRes = await confirmerDetector.DetectAsync(confirmer, bboxCrop.CropPath, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Cascade confirmer '{Model}' failed on bbox {X},{Y} camera '{Camera}'",
                                confirmer.Name, bboxX, bboxY, cameraName);
                            continue;
                        }

                        if (!confRes.Success) continue;

                        // Transformacja: detekcje confirmer są w pikselach bbox crop → offset bbox crop → normalize do klatki
                        var w = bboxCrop.OriginalWidth > 0 ? bboxCrop.OriginalWidth : 1;
                        var h = bboxCrop.OriginalHeight > 0 ? bboxCrop.OriginalHeight : 1;
                        foreach (var d in confRes.Detections)
                        {
                            var absX = d.Box.X + bboxCrop.OffsetX;
                            var absY = d.Box.Y + bboxCrop.OffsetY;
                            results.Add(new DetectionResult(
                                confirmer.Id,
                                d.Label,
                                d.Confidence,
                                new Bbox(absX / w, absY / h, d.Box.Width / w, d.Box.Height / h)));
                        }
                    }
                }
                finally
                {
                    if (bboxCrop is not null) _cropper.Cleanup(bboxCrop);
                }
            }
        }
        finally
        {
            if (roiCrop is not null) _cropper.Cleanup(roiCrop);
        }

        return results;
    }

    /// <summary>Pomocnik — normalizuje ML detekcje do układu [0..1] kamery.</summary>
    private static List<DetectionResult> TransformDetectionsToCamera(
        IEnumerable<(string modelId, MlDetection d)> src,
        int offsetX, int offsetY, int originalW, int originalH)
    {
        var w = originalW > 0 ? originalW : 1;
        var h = originalH > 0 ? originalH : 1;
        var list = new List<DetectionResult>();
        foreach (var (modelId, d) in src)
        {
            var absX = d.Box.X + offsetX;
            var absY = d.Box.Y + offsetY;
            list.Add(new DetectionResult(modelId, d.Label, d.Confidence,
                new Bbox(absX / w, absY / h, d.Box.Width / w, d.Box.Height / h)));
        }
        return list;
    }

    /// <summary>
    /// Zapisuje ActionExecution audit dla każdej akcji która została pominięta przez VLLM gate.
    /// Dzięki temu użytkownik widzi w <c>/admin/actions/history</c> że akcje NIE wypaliły
    /// (z jawnym powodem), a nie że trigger w ogóle nie zadziałał.
    /// </summary>
    private async Task LogVllmSkipAsync(
        IReadOnlyList<DetectionAction> triggerActions,
        ActionContext context,
        ActionSkipReason reason,
        string details,
        CancellationToken ct)
    {
        foreach (var action in triggerActions)
        {
            try
            {
                await _auditLog.LogAsync(new ActionExecution
                {
                    ActionId = action.Id,
                    ActionName = action.Name,
                    TriggerId = context.TriggerId,
                    TriggerName = context.TriggerName,
                    CameraId = context.CameraId,
                    CameraName = context.CameraName,
                    ZoneId = context.ZoneId,
                    ZoneName = context.ZoneName,
                    FrameSnapshotPath = context.FrameSnapshotPath,
                    Status = ActionExecutionStatus.Skipped,
                    SkipReason = reason,
                    ErrorMessage = details,
                    CreatedAt = context.OccurredAt
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to log ActionExecution for VLLM skip");
            }
        }
    }

    /// <summary>
    /// Filtruje detekcje które pasują do zone przez bbox-rule warunków trigger-a.
    /// Detekcja jest dodana jeśli matchuje przynajmniej jeden z condition-ów. Matching
    /// delegowany do <see cref="TriggerConditionMatcher"/> (enforceMinConfidence=false —
    /// confidence gate leży w evaluatorze).
    /// </summary>
    private static List<DetectionResult> FilterDetectionsForZone(
        IReadOnlyList<DetectionResult> all,
        Trigger trigger,
        Zone zone,
        IReadOnlyDictionary<string, SafeView.Domain.Detection.DetectionClass>? detectionClasses)
    {
        var polygon = zone.Polygon.Select(p => (p.X, p.Y)).ToList();
        var result = new List<DetectionResult>();

        foreach (var d in all)
        {
            foreach (var cond in trigger.Conditions)
            {
                if (!TriggerConditionMatcher.Matches(cond, d, detectionClasses, enforceMinConfidence: false))
                    continue;

                var inZone = BboxRuleEvaluator.Evaluate(
                    cond.BboxRule, d.Bbox, polygon,
                    cond.CornersRequired, cond.IouThreshold);
                if (inZone)
                {
                    result.Add(d);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Zapisuje <see cref="SafeView.Domain.Incidents.Incident"/> do kolekcji <c>incidents</c> —
    /// trwała historia detekcji. Każdy wpis ma pełny kontekst (kamera, ROI, strefa, trigger)
    /// + zrzut detekcji (bbox + label + confidence) + ścieżkę do klatki dowodowej.
    /// Failure jest logowany jako Warning — nie przerywa dispatch'u akcji.
    /// </summary>
    private async Task CreateIncidentAsync(
        Camera camera,
        Zone zone,
        Trigger trigger,
        List<DetectionResult> detections,
        IReadOnlyList<SafeView.Domain.Detection.Roi> rois,
        string framePath,
        string? frameRelativePath,
        VllmCheckResult? vllmResult,
        IReadOnlyDictionary<string, SafeView.Domain.Detection.DetectionClass>? detectionClasses,
        DateTime occurredAt,
        CancellationToken ct)
    {
        if (_incidents is null) return;

        try
        {
            var roi = rois.FirstOrDefault(r => r.Id == zone.RoiId);

            // Faza 7: bierzemy pierwszy warunek z ustawionym DetectionClassId (pomija mieszane
            // triggery gdzie legacy + class-based są pomieszane — priorytet na class path).
            var classId = trigger.Conditions
                .Select(c => c.DetectionClassId)
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));

            // Avg confidence liczymy z detekcji w strefie — zgodnie z tym co trigger wypalił.
            double? avgConfidence = detections.Count > 0
                ? detections.Average(d => d.Confidence)
                : null;

            var incident = new SafeView.Domain.Incidents.Incident
            {
                CameraId = camera.Id,
                CameraName = camera.Name,
                TriggerId = trigger.Id,
                TriggerName = trigger.Name,
                RoiId = roi?.Id,
                RoiName = roi?.Name,
                ZoneId = zone.Id,
                ZoneName = zone.Name,
                Category = trigger.Name,
                Summary = $"{detections.Count} detekcji w strefie '{zone.Name}' (trigger '{trigger.Name}')",
                OccurredAt = occurredAt,
                FrameRelativePath = frameRelativePath,
                Detections = detections.Select(d => new SafeView.Domain.Incidents.IncidentDetection
                {
                    Label = d.Label,
                    Confidence = (float)d.Confidence,
                    X = (float)d.Bbox.X,
                    Y = (float)d.Bbox.Y,
                    Width = (float)d.Bbox.Width,
                    Height = (float)d.Bbox.Height
                }).ToList(),
                // Faza D: śledzimy którym szablonem VLLM walidował + confidence,
                // żeby móc liczyć statystyki per-szablon (fire rate, FP rate).
                VllmTemplateId = trigger.VllmCheck?.TemplateId,
                VllmConfidence = vllmResult?.Confidence,
                // Faza 7: analog dla DetectionClass — pozwala liczyć FP rate per-klasa.
                DetectionClassId = classId,
                DetectionConfidence = avgConfidence
            };
            _ = detectionClasses; // resolver zostawiony jako param dla przyszłych rozszerzeń
            await _incidents.InsertAsync(incident, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to create Incident for trigger {Trigger} on camera {Camera}",
                trigger.Name, camera.Name);
        }
    }
}
