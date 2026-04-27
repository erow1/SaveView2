# SafeView — Mapa stanu implementacji

**Ostatnia aktualizacja**: 2026-04-26
**Status**: Fazy 1-5 (pipeline) + Dashboard + Batch + Secrets + GPU + Homografia + SpatialFilters + VLLM szablony/playground/quality-loop + Multi-provider LLM + Monitor wall + Flow topology + Open-vocabulary detection (7 faz: DetectionClass library, YOLO-World Apache 2.0, compiled prompt packs, YOLOE swap-ready visual prompts, quality loop per klasa) + LLM config consolidation (single source of truth = `/admin/llm-providers`) + **ApiCamera push-based ingest (CameraTransport.Api + IngestEndpoints + auto-provisioned full-frame ROI/Zone)**. **209/209 testów zielone**.

## Stan testów

| Projekt | Liczba testów | Czas |
|---------|--------------|------|
| SafeView.Domain.Tests | **105** | ~60ms |
| SafeView.Application.Tests | **89** | ~140ms |
| SafeView.ML.Tests | **15** | ~85ms |
| SafeView.Web.Tests | 0 (stub) | — |
| **Razem** | **209** | <300ms |

**Build status**: 0 errors, 0 warnings (cały solution).

---

## Ukończone fazy pipeline'u (jak było)

### Fazy 1-5 — Core detection + SAHI + VLLM + Actions + Cascade
Architektura `Camera → ROI → Zone → Trigger → [VLLM] → Action`. Szczegóły w `DETECTION_PIPELINE.md`. Domyślny flow niezmieniony od 2026-04-20. Encje: `Roi`, `Zone` (z RoiId+TriggerIds), `Trigger` (Conditions AND + Cooldown + PersistentFrames + Schedule + VllmCheck), `DetectionAction` (LogAlert/InApp/Email/Webhook/Relay/Sms), `ActionExecution` (audit, TTL 30d). SAHI tiling, VLLM gating, Cascade mode (proposer+confirmer).

### Dashboard wydajności
Rolling-window metryki per camera/ROI/model. `/admin/performance` z KPI cards + 4 breakdown tables + auto-refresh 5s.

### Bezpieczeństwo (runtime hardening)
Global authorization fallback, rate-limiting `login` + `api-key`, cookie SameSite=Strict/HttpOnly/8h, audit trail 3-warstwowy (`audit_log` + `system_events` TTL 7d + `action_executions` TTL 30d).

### Logowanie zdarzeń systemowych
`SystemEventSink` (Serilog → Mongo). `/admin/system-events` z filtrami.

### Bundled binaries & models
Runtime/binaries/{rid}/ + runtime/models/{name}/. Seederzy. Skrypty `download-*.sh`.

### Secrets encryption (Faza 4)
`ISecretCipher` + AES-GCM. `Action.Config` sensitive keys (password/secret/token/api_key) szyfrowane w Mongo.

### GPU acceleration (opcjonalne)
`MLOptions.UseGpu/GpuDeviceId/FallbackProvider`. Fallback chain: CUDA → DirectML/CoreML → CPU. Wymaga `Microsoft.ML.OnnxRuntime.Gpu` (opt-in NuGet ~1.5GB).

---

## Nowe od 2026-04-20 (Fazy 6+)

### Homografia i filtry przestrzenne
**Domain** (`SafeView.Domain.Cameras`):
- `CalibrationPoint { PixelX, PixelY, WorldX, WorldY }`
- `HomographyMatrix` (3×3 z `Project(px, py) → (Xm, Ym)?`)
- `HomographyCalculator.Compute(IReadOnlyList<CalibrationPoint>)` — DLT (Direct Linear Transform) + least-squares przez normalne równania + Gauss z partial pivoting. Min 4 punkty, null gdy osobliwe. 5 testów jednostkowych.
- `Camera.CalibrationPoints` — lista punktów kalibracji per kamera.

**Domain** (`SafeView.Domain.Detection`):
- `SpatialFilter { Kind, ModelIdA, LabelsA, ModelIdB, LabelsB, MinDistanceM?, MaxDistanceM?, PairMode }`
- `SpatialPairMode { AnyPair, AllPairs }`
- `Trigger.SpatialFilters` — lista filtrów (AND między nimi).

**Application** (`SafeView.Application.Detection`):
- `SpatialFilterEvaluator.Evaluate(filters, detections, homography)` — rzutuje stopę bboxa przez homografię na metry, ocenia pary. Zwraca `Result(Passed, FailReason?)`.
- `DetectionPipeline` — gate po trigger fire + przed VLLM. Homografia liczona raz per-klatka.

**UI**:
- Strona `/cameras/{id}/calibration` — dwa tryby: **Prostokąt** (4 kliki + W/D w metrach, auto-przypisanie coords (0,0)/(W,0)/(W,D)/(0,D)) albo **Punkty** (dowolne XY ręcznie). Przy save walidacja przez `Compute` (odrzut gdy koliniarne).
- Ikona "linijka" (`SquareFoot`) w `/cameras` per-wiersz.
- TriggerDialog — sekcja **"Filtry przestrzenne"** z pairdistance + dropdown modeli + labele + min/max (m) + tryb par.

### Monitor wall (`/monitor`)
**Przepisane**: dropdown zamieniony na **grid kafli** — każda kamera = MudPaper z aspect 16:9 + overlay + opis. Klik w kafel → `CameraDialog` edycja.
- Multi-tile JS engine ([monitor.js](../src/SafeView.Web/wwwroot/monitor.js)) — `Map<tileId, {token, timer, abort, ctx}>`, per-kafel session token + AbortController.
- Globalne toggles (shared): ROI / Strefy / Detekcje / Dystanse (+ slider N par) / Sync frame / Wszystkie klasy (debug) / Interval slider.
- Overlay SVG + HTML labels: rect dla bbox + pasek z label+confidence, linie dystansu z etykietami w metrach (tylko gdy kamera skalibrowana), filtr klas zgodny z triggerami (default) lub wszystkie (debug switch).
- Synchronizacja klatki: `/cameras/{id}/pipeline-frame.jpg` serwuje klatkę z ostatniego pipeline run (bboxy zgodne z obiektem) albo `/cameras/{id}/snapshot.jpg` (live, bboxy latają).

**Endpoint**: `GET /api/monitor/{cameraId}?all=true` — zwraca ROI + zones + filtered detections + `homography` (gdy skalibrowana) + `capturedAt`.
**Store**: `IDetectionSnapshotStore` (singleton in-memory) zapisywany przez pipeline po każdym `allDetections`, niezależnie czy trigger fired.
**Nowe pole**: `DetectionSnapshot.FrameRelativePath` — wskazuje klatkę dla pipeline-frame endpoint.

### Flow pipeline (`/flow`)
Topologia systemu jako graf Cytoscape.js + live events przez SignalR.
- Nody: kamera / ROI / strefa / trigger / akcja / model — dark panel z akcentowym tłem (tint koloru typu), ramka w kolorze akcentu, dwulinijkowy label (TYP + nazwa), theme-aware (light + dark).
- Edge: camera→roi, roi→zone, roi→model, zone→trigger, trigger→action.
- **Live events** przez [FlowHub](../src/SafeView.Web/Hubs/FlowHub.cs) (SignalR, `/hubs/flow`):
  - `TriggerFired(triggerId, zoneId, cameraId, detectionCount)`
  - `ActionExecuted(actionId, triggerId, status, durationMs)`
  - `VllmRejected(triggerId, zoneId, cameraId)`
  - `SnapshotCaptured` (rezerwa, ignorowany w JS)
- Animacje: pulse node + animowany dash-offset "przepływu" przez edge.
- **Klik w node** → deep-link `?edit={id}&returnTo=/flow` — strona CRUD auto-otwiera dialog, po zamknięciu wraca na /flow.

**Vendor libs offline** (w `wwwroot/vendor/`): cytoscape, dagre, cytoscape-dagre, @microsoft/signalr. Piny w `scripts/download-vendor.sh`.

### VLLM — Fazy A-D (pełna pętla jakości)

**Faza A — Biblioteka szablonów** (`/admin/vllm-templates`)
- `PromptTemplate` entity — Name/Description/Category/SystemPrompt(EN)/UserTemplate(PL)/SchemaJson/RecommendedMinConfidence/DefaultMinSeverity/RequireGoodImageQuality/IsBuiltIn/BuiltInKey.
- `PromptTemplateSeeder` + `BuiltInTemplates.All()` — 12 scenariuszy BHP: PPE kask / PPE kamizelka / Pożar / Intruz / Upadek / Wózek-pieszy / Osłona maszyny / Skupienie / Rozlanie / Wysokość / Palenie / Loitering. Każdy z skalibrowanym promptem (CoT + anti-FP + severity rubric + image quality self-check).
- **Nowy schema odpowiedzi** (strict): `observations[] / alternative_explanations[] / severity (none-critical) / confidence (0-1) / confirmed / image_quality (good/acceptable/poor) / reason / recommended_action (ignore-emergency)`.
- `VllmChecker` przepisany: parsuje nowy schema + backward-compat. **Triple gate**: `confidence >= min` AND `severity >= min` AND `(quality != poor OR !RequireGoodImageQuality)`.
- `VllmCheckConfig` rozszerzony: `TemplateId?`, `SchemaJson?`, `MinSeverity`, `RequireGoodImageQuality`, `LlmProviderId?`.
- Built-in re-seeding safe: identyfikacja przez `BuiltInKey`, user edytuje przez Duplikuj.

**Faza B — Playground** (`/admin/vllm-playground`)
- 3-kolumnowy layout: config (dropdown szablonu + edycja pól + gates + schema) | upload obraz (JPG/PNG max 20MB) | wynik (severity chip + confidence bar + quality + action + observations + alternatives + raw JSON).
- Woła `IVllmChecker.CheckAsync` bezpośrednio na syntetycznym `ActionContext` z tempfile. Timing mierzenia.
- Dropdown **LLM providera** — pozwala testować ten sam prompt na różnych modelach.

**Faza C — Generator + Refine**
- `IPromptGenerator.GenerateAsync(description)` — meta-prompt (~2KB) z 9 regułami (CoT / anti-FP / calibration / quality / severity rubric) + few-shot. Zwraca strict JSON matching `PromptTemplate`.
- `IPromptGenerator.RefineAsync(current, feedback)` — dostaje obecny szablon jako JSON + feedback użytkownika, zwraca updated (preserve non-mentioned). Przyciski **Wygeneruj z opisu** / **Popraw z feedbackiem** w playgroundzie + **Zapisz jako szablon**.

**Faza D — Quality loop**
- `Incident` += `VllmTemplateId? / VllmConfidence? / WasFalsePositive / FalsePositiveAt / FalsePositiveByUserId`.
- Pipeline: `DetectionPipeline.CreateIncidentAsync` dopina VLLM meta do incidentu.
- Statystyki per-szablon: `IIncidentRepository.GetStatsByVllmTemplateAsync(templateId, from)` zwraca `VllmTemplateStats(FireCount, FalsePositiveCount, AvgConfidence, LastFireAt, ConfidenceHistogram[10])`.
- `/admin/vllm-templates` kolumna **"Ostatnie 30 dni"** z 3 chipami: wypalenia / FP% (kolor: <10% green, <30% amber, >30% red) / avg conf.
- **Drill-down dialog** (`VllmTemplateStatsDialog`): 3 KPI cards + MudChart bar (histogram 10 bucketów) + **historia wersji** z przyciskiem Przywróć.
- **Auto-versioning**: `MongoPromptTemplateRepository.UpdateAsync` override — przed zapisem snapshot poprzedniego stanu do `prompt_template_versions`. Retention: 50 ostatnich.
- **Import/Export JSON**: endpointy `/api/vllm/templates/{id}/export`, `/export-all`, `POST /import`. UI: przyciski + MudFileUpload.
- **Feedback przez incident**: `/incidents/{id}` ma zunifikowany przycisk **"Fałszywy alarm"** który ustawia `WasFalsePositive=true` ORAZ zamyka incident (`Status=FalsePositive + ResolvedAt/By`). Wcześniejszy dualizm "Rozwiąż jako fałszywy" zlikwidowany.

### Multi-provider LLM (single source of truth, 2026-04-26 consolidation)
Single source of truth: encja `LlmProvider` w Mongo, edytowana wyłącznie przez `/admin/llm-providers`. Brak duplikacji konfiguracji — appsettings sekcja `Llm`, strona `/admin/llm` (`LlmAdmin.razor`) i `LlmProviderSeeder` zostały zlikwidowane.
- `LlmProvider` entity + `LlmProviderKind` enum (Ollama/LmStudio/Vllm/LocalAi/OpenAI/AzureOpenAI/Groq/TogetherAi/MistralAi/DeepSeek/OpenAiCompatible).
- Strona `/admin/llm-providers` — CRUD z **presetami per typ** (Ollama → localhost:11434/v1 + qwen2.5-vl:7b; OpenAI → api.openai.com/v1 + gpt-4o-mini; itd.). Przycisk **Test połączenia** (ping `/models`). Flag `IsDefault` (tylko jeden; zmiana wyłącza poprzedniego).
- `IChatClientFactory.GetForAsync(providerId)` — cache per providerId, per-provider dedicated `HttpClient`. Null/empty providerId → resolwuje providera z `IsDefault=true`. Brak skonfigurowanego providera → `InvalidOperationException` z linkiem do strony konfiguracji. `Invalidate(id)` po CRUD.
- `IEmbeddingsClientFactory` analogicznie (CLIP encoder external strategy).
- `VllmChecker` używa wyłącznie `IChatClientFactory` (drop legacy fallback `IChatClient`).
- `VllmCheckConfig.LlmProviderId?` + dropdown providera w TriggerDialog i playground.
- Konsumenci po refactorze: `Assistant.razor`, `CameraDetail.razor`, `LlmProviders.razor`, `IncidentAnalyzer`, `LlmPromptGenerator`, `VllmChecker` — wszyscy przez factory.
- `LlmOptions` pozostaje jako wewnętrzny DTO budowany przez factory z `LlmProvider` (nie jest bindowany z appsettings).
- `Assistant.razor` + `CameraDetail.razor.SummarizeAsync` mają empty-state gdy brak providera (link do `/admin/llm-providers`).

### In-app notifications
`IInAppNotificationBroker` singleton → event Published. `MainLayout` subskrybuje, pokazuje `MudSnackbar.Add` u każdego aktywnie zalogowanego usera. `InAppNotificationHandler` publikuje + logi. Config key `severity` (info/success/warning/error).

### Test-fire dla Actions/Triggers
`IActionDispatcher.TestFireAsync` — pomija Enabled + rate-limit gate, audit log pozostaje. Przyciski Play w `/admin/actions` (single action) i `/admin/triggers` (wszystkie akcje triggera, liczy OK/fail).

### Menu podzielone na 3 sekcje (MudNavGroup)
- **Operator**: Dashboard, Podgląd, Flow pipeline, Incydenty, Raporty, Asystent
- **Konfiguracja**: Kamery, ROI, Strefy, Triggery, Akcje, Modele, Szablony VLLM, Playground VLLM, Dostawcy LLM, MediaMTX, Ustawienia
- **Administracja** (zwinięte): Użytkownicy, API Keys, Licencja, Audit log, Historia akcji, System events, Performance

### Sampler: adaptacyjny sleep
Usunięto globalny `CameraRefreshSeconds` — sampler budzi się zgodnie z najbliższym `nextDue` per-kamera (na podstawie `SnapshotIntervalSeconds`), ograniczony dołem przez `MinTickMilliseconds` (default 250ms). Każda kamera działa w swojej własnej kadencji.

### Swagger / OpenAPI (2026-04-26)

`Swashbuckle.AspNetCore` 7.2.0 — offline-first (assets bundled w NuGet). Dokumentacja dla `/api/v1/*` endpointów, reszta odfiltrowana przez `DocInclusionPredicate`. Cookie-auth gate na UI (anon → redirect /login). Security scheme `ApiKey` (Bearer) globalny w spec — UI ma "Authorize" button do testowania endpointów z API key.

**Endpoint metadata** (`.WithTags/.WithSummary/.WithDescription/.Produces/.ProducesProblem`) dodana do wszystkich endpointów w `ApiV1Endpoints` (Incidents/Cameras/Zones/Health) i `IngestEndpoints` (multipart + JSON-only). Grupy w UI:
- **Incidents** — list w przedziale czasu, get by ID
- **Cameras** — list metadanych
- **Zones** — list opcjonalnie filtrowana per kamera
- **Health** — `/api/v1/ping`
- **Ingest** — push-based dla `CameraTransport.Api`

Nav menu: Administracja → "API docs (Swagger)" otwiera w nowej karcie.

### ApiCamera — push-based ingest (2026-04-26)

Nowy typ kamery `CameraTransport.Api` — zewnętrzny system (edge appliance, Frigate, własny inference server) wysyła klatki + pre-computed detekcje przez REST. SafeView nie polluje, tylko czeka na push.

**Domain** (`SafeView.Domain`):
- `CameraTransport.Api` — nowa wartość enum
- `CameraVendor.ApiPush = 100` — analog `FileSource = 99`, wymusza `Transport.Api`
- `Camera.IngestApiKeyId?` — opcjonalny per-camera pin (defense-in-depth ponad scope)
- `Roi.IsFullFrame` — flaga że ROI pokrywa cały kadr (geometria nie-edytowalna w UI)
- `Permission.ApiCamerasWrite = "api:cameras:write"` — scope dla ingest endpoint

**Application**:
- `IDetectionPipeline.ProcessExternalDetectionsAsync(camera, framePath, frameRelPath, externalDetections, capturedAt, ct)` — push-based entry point. Pomija stage inferencji, wpada do wspólnej `EvaluateAndDispatchAsync` używanej też przez `ProcessFrameAsync`. Downstream nie wie że źródło zewnętrzne.
- `IApiCameraProvisioner` + `ApiCameraProvisioner` — auto-tworzy pełnokadrową ROI (`Rectangle=(0,0,1,1)`) + Zone (4-punkt polygon) przy save kamery typu Api. Idempotentne.

**Web**:
- `IngestEndpoints` (`POST /api/v1/cameras/{id}/ingest` multipart + `/ingest-json` z base64/URL fallback). Auth scope `api:cameras:write`. Walidacja bbox-bounds, confidence, image size 20MB, JSON 1MB. Per-camera pin gate. Idempotency po `frame_id`.
- `IngestIdempotencyStore` — in-memory LRU 1000 frame-ów per kamera (reset przy restarcie; production-grade Mongo TTL collection w follow-up #51).
- `IngestDtos` — schema `1.0` zbliżona do Roboflow Inference: `frame{width,height,captured_at,frame_id?}, detections[]{label,confidence,bbox{x,y,w,h pixel coords},track_id?,polygon?,keypoints?,attributes?}, source?, image_base64?, image_url?`. Bbox w pixel coords — server normalizuje do `[0..1]`. `ModelId="external"` constant.
- Rate-limit policy `camera-ingest` partycjonowany per `cameraId` (1800 req/min).

**UI**:
- `CameraDialog`: Vendor=ApiPush → ukrywa pola RTSP/HTTP/File-source, pokazuje panel z URL endpointu + 2 expansion panels (curl multipart + JSON-only) + pole `IngestApiKeyId` pin. Hide Sampling section (sampler nie polluje).
- `Cameras.razor` save handler: po `Insert/Update` z `Transport=Api` woła `ApiProvisioner.EnsureFullFrameRoiAndZoneAsync(cam.Id)` — pełnokadrowa ROI + Zone tworzona automatycznie.
- `CameraFrameSampler` — guard `c.Transport != Api` żeby skip-nąć (push-based).

**Format**: pixel coords w bbox (sender ma je z YOLO output), pomocniczy `frame_id` dla idempotency, `source.name/model/inference_ms` dla audytu, `attributes` wolny słownik dla sender-specific danych. Schema versioned przez URL `/api/v1/` + body `schema_version` field.

**Tests**: 5 testów `ApiCameraProvisionerTests` (idempotency, partial state, full state, ROI bez Zone, walidacja).

**Pełna dokumentacja**: `documentation/API_INGEST.md` (curl examples, error codes, smoke test E2E).

### Open-vocabulary detection (2026-04-23, 7 faz)

**Motywacja**: tradycyjne closed-set YOLO wymaga retreningu dla każdej nowej klasy BHP. Otwarto ścieżkę "type and compile" — operator opisuje klasę tekstem albo pokazuje przykładami, bez ML-dev-loop.

**Faza 1 — DetectionClass fundament** (Domain + repo + seeder)
- `DetectionClass` entity (Domain/Detection) — pierwszoklasowa encja klasy, odklejona od `MLModel.Labels`. Kind: `ClosedSetBinding / Text / Visual / TextAndVisual`. `VisualReferences[]` (bez embedding cache w domain — cache per-detector).
- `ModelCapabilities` enum `[Flags]`: `ClosedSet / TextPrompts / VisualPrompts`. Istniejące modele default `ClosedSet` (backward-compat).
- `MLModel` +: `Capabilities`, `SourceModelId`, `CompiledClassIds`.
- `TriggerCondition.DetectionClassId?` — nowy alt do legacy `ModelId+Labels`. Backward-compat: stare triggery działają 1:1.
- `TriggerConditionMatcher` (Application) — wspólny matcher dla pipeline + evaluator. Obsługuje oba path-y (legacy + class-based).
- `DetectionClassSeeder` + `BuiltInDetectionClasses` — 14 klas BHP (PPE: kask / kamizelka / rękawice / okulary; Pożar: płomień / dym / rozlanie; Ruch: osoba / wózek / pojazd; Bezpieczeństwo: gaśnica / szafa rozdzielcza). Read-only, `BuiltInKey` re-seed-safe.
- Permisja `admin:detection-classes` (auto-rejestrowana przez `AddSafeViewPolicies`).

**Faza 2 — Interfejsy detektorów** (rozwarstwienie capability)
- `ITextPromptDetector` (Application/Abstractions/ML) — `DetectWithPromptsAsync(model, image, prompts[])`.
- `IVisualPromptDetector` — `DetectWithVisualPromptsAsync(model, image, VisualPromptClass[])` + `InvalidateClassCache(classId)`.
- `IDetectorFactory` +: `GetTextPromptDetector(model)` / `GetVisualPromptDetector(model)` — capability-gate + backend resolve po `IObjectDetector.Backend` string. Plug-in pattern: rejestracja w DI = dostępne przez factory.
- `DetectorFactory` (SafeView.ML) — switch po `DetectorBackend`, resolve per backend name.

**Faza 3 — YOLO-World detector** (Apache 2.0, primary open-vocab)
- `OnnxYoloWorldDetector` (SafeView.ML/YoloWorld) — impl `IObjectDetector + ITextPromptDetector + IBatchObjectDetector`. Auto-detect dynamic (2 inputs) vs compiled (1 input). 
- `IClipTextEncoder` (Application) — hot-swap między 2 implementacjami:
  - `OnnxClipTextEncoder` (in-process, default) — CLIP ViT-B/32 bundled w `runtime/models/yolo-world-v2-s/text-encoder.onnx`.
  - `ExternalLlmClipTextEncoder` — via `IEmbeddingsClient` + provider OpenAI-compat (wymaga CLIP-kompatybilny model, np. self-hosted CLIP-as-a-service).
- `ClipTokenizer` — self-contained CLIP BPE, zero external deps (`Microsoft.ML.Tokenizers` celowo uniknięte). Vocab + merges w `tokenizer/`.
- `IEmbeddingsClient + IEmbeddingsClientFactory` (Application) + `OpenAiCompatibleEmbeddingsClient + EmbeddingsClientFactory` (SafeView.LLM) — pattern 1:1 z `IChatClientFactory`.
- `YoloWorldOptions` — `EncoderStrategy` (InProcessOnnx / ExternalLlm), `ExternalLlmProviderId`, `TextContextLength`.
- `DetectBatchAsync`: N obrazów w 1× Run(), text embeddings liczone raz + replikowane do batch-dim (SAHI use case).
- `scripts/download-models.sh` +: target `yolo-world-v2-s` (~200MB z HF).
- `ModelSeeder` auto-rozpoznaje strukturę folderu → `Backend=YoloWorld, Capabilities=ClosedSet|TextPrompts`.

**Faza 4 — UI biblioteka klas + TriggerDialog refactor**
- `/admin/detection-classes` — lista z groupowaniem po kategorii, built-in read-only, Duplikuj / Edit / Delete.
- `DetectionClassDialog` — per-kind fields (Text prompt EN/PL, ClosedSetBinding model+label z chipami klikalnych etykiet, Visual refs upload).
- `TriggerDialog` — switch "Użyj klasy z biblioteki" per-warunek. Class-based: dropdown klas pogrupowanych + chip kindu + auto-fill MinConfidence. Legacy: stary flow bez zmian.
- Nav menu: `Konfiguracja → Klasy detekcji`.

**Faza 5 — Compiled prompt packs** (persistent embeddings cache)
- `CompiledPromptPack` entity — `(SourceModelId, EncoderBackend, ClassIds[], Prompts[], EmbeddingsBlob byte[])`. Trwały cache pre-computed CLIP embeddings.
- `IPromptPackCompiler + PromptPackCompiler` — `CompileAsync(sourceModelId, classIds)` → encode raz + zapis. `RefreshStalenessAsync` — porównuje `PromptSnapshots` z aktualnymi textprompts klas, marks stale.
- `OnnxYoloWorldDetector` — pre-check `ICompiledPromptPackRepository.FindByPromptsAsync` przed encode. Cache hit = skip CLIP (~10-30ms oszczędności per klatka). Graceful fallback gdy lookup failuje.
- `/admin/prompt-packs` — lista packów z statusem Fresh/Stale, przycisk "Kompiluj" (multi-select klas z checkbox + wybór YOLO-World modelu).
- Decyzja architektoniczna: **nie robimy prawdziwego re-param ONNX grafu** (wymagałby Python sidecar / OnnxSharp zależność). Persistent embeddings cache daje identyczny zysk performance bez tego narzutu. Follow-up ticket dla true re-param gdy pojawi się realna potrzeba.

**Faza 6 — Visual-prompt backend** (swap-ready)
- `FileKind.DetectionClassRef` — nowa kategoria storage. Ścieżki: `detection-classes/{classId}/refs/{guid}.{ext}`.
- `DetectionClassRefEndpoints` — `GET/POST/DELETE /api/detection-classes/{classId}/refs/{refName}`. Path-traversal safe (regex walidacja identyfikatorów + Guid filename server-side). Max 5MB, jpg/png.
- `DetectionClassDialog` sekcja Visual — grid thumbnails 120×120, upload przez IJSRuntime + fetch(FormData), delete inline.
- `OnnxYoloEDetector` (SafeView.ML/YoloE) — **AGPL, swap-ready**. Impl 3 interfejsów. 2 sessions: detection head + image encoder (ViT-224 ImageNet norm). Visual flow: refs per class → encode → mean + L2-normalize → per-class embedding → feed do detection head. Cache `ConcurrentDictionary<string classId, float[]>` + `InvalidateClassCache`.
- `scripts/download-models.sh` +: target `yoloe-11s` z **interaktywnym AGPL warningiem** (user musi wpisać "y"). README bundled z jasnym licence note.
- `ModelSeeder` rozpoznaje 3 typy folderów: YoloE (text+image encoders+tokenizer) / YoloWorld (text+tokenizer) / Onnx (default).
- **Architektura swap-ready dla OWLv2 w przyszłości** — dokumentowana w docstring `DetectorBackend.YoloE`: dodać enum wartość + impl `IVisualPromptDetector` + DI rejestracja = gotowe. Zero zmian w Domain, Trigger, UI.

**Faza 7 — Quality loop per DetectionClass**
- `Incident` +: `DetectionClassId?` + `DetectionConfidence?` (avg z detekcji). Index `DetectionClassId + OccurredAt desc`.
- `DetectionPipeline.CreateIncidentAsync` wypełnia pola — bierze pierwszy `DetectionClassId` z `trigger.Conditions` + liczy avg confidence.
- `IIncidentRepository.GetStatsByDetectionClassAsync(classId, from)` + `DetectionClassStats` record (FireCount / FpCount / AvgConfidence / LastFireAt / ConfidenceHistogram[10] / FalsePositiveRate).
- `/admin/detection-classes` +: kolumna "Ostatnie 30 dni" z 3 chipami (fires / FP% z color-coding / avg conf).
- `DetectionClassStatsDialog` — 3 KPI cards + MudChart histogram 10 bucketów + selektor okna (7/30/90 dni).

### Incident page clean-up
- `/incidents` — klikalne wiersze (row click → `/incidents/{id}`), kolumna akcji usunięta (przeniesione do karty).
- `/incidents/{id}` — pasek akcji z przyciskami: **Analiza LLM** (Primary/Outlined) / **Potwierdź** (Info) / **Rozwiąż** (Success) / **Fałszywy alarm** (Warning). Wszystkie `Variant.Outlined`. Confirm dialog dla zamykających akcji.
- Overlay bboxów na klatce dowodowej (SVG + HTML labels).
- Endpoint `/incidents/{id}/frame.jpg` — IFileStore odczyt, path-traversal-safe.
- Chipy severity/status przez **MudBlazor native Color** (theme-aware). Nadpisy dla outlined w `theme-light.css` (info/success/warning/error).

---

## Mapa endpointów (aktualne)

### Publiczne (rate-limited)
- `POST /auth/login`, `/auth/setup`, `/auth/logout`
- `GET /health/live`, `/health/ready`
- `GET /culture/set?culture=pl|en&redirect=/...`

### Push-based ingest (scope `api:cameras:write`, rate `camera-ingest`)
- `POST /api/v1/cameras/{cameraId}/ingest` — multipart (frame binary + JSON metadata)
- `POST /api/v1/cameras/{cameraId}/ingest-json` — JSON-only z `image_base64` lub `image_url`

### OWLv2 — sole open-vocab detector (2026-04-27)

YOLO-World v2 USUNIĘTY 2026-04-27 (false positives na rzadkich klasach). OWLv2 (Google, Apache 2.0) jest teraz jedynym open-vocab path.

**Dwa warianty**:
- `runtime/models/owlv2-base/` — patch16 ensemble (~614MB, 153M params, 960×960). Fast — dla iteracji / dev-loop.
- `runtime/models/owlv2-large/` — patch14 ensemble (~1.74GB, 430M params, 960×960). Best quality — dla produkcji. ~3-4× wolniejszy CPU.

Oba: single fused ONNX, 3 inputs (`pixel_values`, `input_ids`, `attention_mask`), output `logits` (sigmoid → scores) + `pred_boxes` (cxywh-norm). `DetectorBackend.OwlV2 = 4`, single impl `src/SafeView.ML/OwlV2/OnnxOwlV2Detector.cs`. ModelSeeder rozpoznaje folder po `preprocessor_config.json` + `tokenizer/`. User przełącza w `/models` test detect dropdown — pipeline agnostic.

### Swagger / OpenAPI (cookie-auth)
- `GET /swagger` — interactive Swagger UI (Swashbuckle bundle, offline-first)
- `GET /swagger/v1/swagger.json` — OpenAPI 3.0 spec dla wszystkich `/api/v1/*` endpointów

### Open-vocabulary detection (permisja `admin:detection-classes`)
- `GET /api/detection-classes/{classId}/refs/{refName}` — serwuje crop referencyjny (thumbnail)
- `POST /api/detection-classes/{classId}/refs` — upload visual ref (multipart, max 5MB)
- `DELETE /api/detection-classes/{classId}/refs/{refName}` — usuwa ref + plik

### Autoryzowane per-permisja
- `GET /cameras/{id}/snapshot.jpg` — `cameras:view`, vendor HTTP CGI proxy + ffmpeg fallback
- `GET /cameras/{id}/pipeline-frame.jpg` — `cameras:view`, serwuje klatkę z ostatniego pipeline run
- `GET /incidents/{id}/frame.jpg` — `incidents:view`, IFileStore odczyt (path-traversal safe)
- `GET /reports/incidents.{format}` — `reports:generate`
- `GET /api/metrics/performance?windowMinutes=N` — `admin:system-events`
- `GET /api/monitor/{cameraId}?all=bool` — `cameras:view`, ROI+zones+detections+homography
- `GET /api/flow/topology` — `cameras:view`, nodes+edges dla Cytoscape
- `GET /api/vllm/templates/{id}/stats?windowDays=N` — `llm:configure`, fireCount/FP/histogram
- `GET /api/vllm/templates/{id}/export`, `/export-all`, `POST /import` — `llm:configure`
- `GET /api/v1/incidents|cameras|zones|ping` — API key

### SignalR Hubs
- `/hubs/flow` — `FlowHub` (broadcast live pipeline events), policy `cameras:view`

### Strony Blazor
Operator: `/` (Home), `/monitor`, `/flow`, `/incidents`, `/incidents/{id}`, `/reports`, `/assistant`
Cameras: `/cameras`, `/cameras/{id}/live`, `/cameras/{id}/detail`, `/cameras/{id}/calibration`
Detection: `/zones`, `/admin/rois`, `/admin/triggers`, `/admin/actions`, `/admin/actions/history`, `/models`, **`/admin/detection-classes`, `/admin/prompt-packs`**
LLM: `/admin/llm-providers` (single source of truth, multi-provider), `/admin/vllm-templates`, `/admin/vllm-playground`
Admin (perm:admin:*): `/users`, `/license`, `/audit`, `/admin/settings`, `/admin/system-events`, `/admin/performance`, `/admin/mediamtx`, `/api-keys`
Auth: `/login`, `/setup`, `/forbidden`, `/not-found`, `/Error`
Account: `/account/password`

---

## Kolekcje MongoDB (aktualne)

| Kolekcja | Encja | Index | TTL |
|----------|-------|-------|-----|
| `users` | User | Username/Email unique | — |
| `roles` | Role | Name unique | — |
| `cameras` | Camera | — | — |
| `zones` | Zone | CameraId, RoiId | — |
| `rois` | Roi | CameraId, Enabled | — |
| `triggers` | Trigger | Enabled | — |
| `actions` | DetectionAction | Enabled | — |
| `action_executions` | ActionExecution | CreatedAt/TriggerId/ActionId/CameraId | **30d** |
| `ml_models` | MLModel | — | — |
| `incidents` | Incident | OccurredAt, CameraId+OccurredAt, Status+OccurredAt, VllmTemplateId+OccurredAt, **DetectionClassId+OccurredAt** | — |
| `api_keys` | ApiKey | KeyHash unique | — |
| `audit_log` | AuditEntry | CreatedAt desc, Username, Action | — |
| `system_events` | SystemEvent | CreatedAt, Severity, Source | **7d** |
| `prompt_templates` | PromptTemplate | Category, BuiltInKey, Name | — |
| `prompt_template_versions` | PromptTemplateVersion | TemplateId+CreatedAt desc | — |
| `llm_providers` | LlmProvider | IsDefault, Name | — |
| `detection_classes` | DetectionClass | Category, BuiltInKey, Name, (ClosedSetModelId+ClosedSetLabel) | — |
| `compiled_prompt_packs` | CompiledPromptPack | SourceModelId, Name | — |

**Hosted services** (IHostedService):
- `LegacyZoneCleanupService` — drop starych Zone bez RoiId (jednorazowo)
- `ModelSeeder` — auto-rejestracja bundled modeli ONNX
- `IdentitySeeder` — role built-in + sync permisji
- `MediaMtxRunner` — supervisor MediaMTX
- `CameraFrameSampler` — adaptacyjny per-kamera
- `DailyDigestJob` — codziennie 06:00 UTC
- **`PromptTemplateSeeder`** — 12 built-in szablonów VLLM
- **`DetectionClassSeeder`** — 14 built-in klas BHP (Faza Open-Vocab #1)

---

## Vendor libs offline

W `src/SafeView.Web/wwwroot/vendor/` (commitowane):
- `cytoscape.min.js`, `dagre.min.js`, `cytoscape-dagre.js`, `signalr.min.js`

Piny wersji w `scripts/download-vendor.sh`. Dodawanie nowej JS-owej zależności = update skryptu + run + commit.

---

## Nowe moduły/zależności (chronologicznie od 2026-04-20)

```
SafeView.Domain:
  + Cameras/CalibrationPoint.cs
  + Cameras/HomographyMatrix.cs
  + Cameras/HomographyCalculator.cs (DLT)
  + Detection/SpatialFilter.cs
  + Vllm/ResponseSeverity.cs (+ RecommendedAction, ImageQuality enums)
  + Vllm/PromptTemplate.cs
  + Vllm/PromptTemplateVersion.cs
  + Llm/LlmProvider.cs (+ LlmProviderKind enum)

SafeView.Application:
  + Abstractions/Detection/IDetectionSnapshotStore.cs
  + Abstractions/Detection/IFlowEventPublisher.cs
  + Abstractions/Notifications/IInAppNotificationBroker.cs
  + Abstractions/Persistence/IPromptTemplateRepository.cs
  + Abstractions/Persistence/IPromptTemplateVersionRepository.cs
  + Abstractions/Persistence/ILlmProviderRepository.cs
  + Abstractions/Vllm/IPromptGenerator.cs
  + Abstractions/LLM/IChatClientFactory.cs
  + Detection/DetectionSnapshotStore.cs
  + Detection/SpatialFilterEvaluator.cs
  + Notifications/InAppNotificationBroker.cs

SafeView.Infrastructure:
  + Persistence/MongoPromptTemplateRepository.cs (override UpdateAsync → auto-versioning)
  + Persistence/MongoPromptTemplateVersionRepository.cs
  + Persistence/MongoLlmProviderRepository.cs
  + Vllm/PromptTemplateSeeder.cs + BuiltInTemplates.cs (12 szablonów BHP)
  - Llm/LlmProviderSeeder.cs (DELETED 2026-04-26 — single source = DB, brak migracji z appsettings)

SafeView.LLM:
  + ChatClientFactory.cs (cache per-provider HttpClient)
  + LlmPromptGenerator.cs (+ RefineAsync)

SafeView.Web:
  + Endpoints/MonitorEndpoints.cs
  + Endpoints/FlowEndpoints.cs
  + Endpoints/IncidentFrameEndpoint.cs
  + Endpoints/PipelineFrameEndpoint.cs
  + Endpoints/VllmStatsEndpoint.cs
  + Endpoints/VllmTemplateIoEndpoints.cs
  + Hubs/FlowHub.cs + SignalRFlowEventPublisher
  + Components/Pages/Monitor.razor (video wall)
  + Components/Pages/Flow.razor
  + Components/Pages/CameraCalibration.razor
  + Components/Pages/IncidentDetail.razor
  + Components/Pages/VllmTemplates.razor + VllmTemplateDialog + VllmTemplateStatsDialog
  + Components/Pages/VllmPlayground.razor + VllmGeneratorDialog + VllmRefineDialog
  + Components/Pages/LlmProviders.razor + LlmProviderDialog
  + wwwroot/monitor.js (multi-tile engine)
  + wwwroot/flow.js (Cytoscape + SignalR)
```

### Open-Vocabulary Detection (2026-04-23)

```
SafeView.Domain:
  + Detection/DetectionClass.cs (+ DetectionClassKind enum, VisualReference)
  + Detection/CompiledPromptPack.cs
  + ML/MLModel.cs — ModelCapabilities flags, SourceModelId, CompiledClassIds
  + Detection/TriggerCondition.cs — DetectionClassId?
  + Incidents/Incident.cs — DetectionClassId? + DetectionConfidence?

SafeView.Application:
  + Abstractions/ML/ITextPromptDetector.cs
  + Abstractions/ML/IVisualPromptDetector.cs (+ VisualPromptClass)
  + Abstractions/ML/IClipTextEncoder.cs
  + Abstractions/LLM/IEmbeddingsClient.cs + IEmbeddingsClientFactory.cs
  + Abstractions/Persistence/IDetectionClassRepository.cs
  + Abstractions/Persistence/ICompiledPromptPackRepository.cs
  + Abstractions/Persistence/IIncidentRepository.cs — GetStatsByDetectionClassAsync + DetectionClassStats
  + Abstractions/Detection/IPromptPackCompiler.cs
  + Abstractions/Storage/IFileStore.cs — FileKind.DetectionClassRef
  + Detection/TriggerConditionMatcher.cs
  + Detection/PromptPackCompiler.cs
  + Configuration/StorageOptions.cs — DetectionClassRefs path

SafeView.Infrastructure:
  + Persistence/MongoDetectionClassRepository.cs
  + Persistence/MongoCompiledPromptPackRepository.cs
  + Detection/BuiltInDetectionClasses.cs (14 klas BHP)
  + Detection/DetectionClassSeeder.cs
  + ML/ModelSeeder.cs — auto-detect YoloWorld / YoloE / Onnx per folder structure

SafeView.ML:
  + YoloWorld/OnnxYoloWorldDetector.cs
  + YoloWorld/OnnxClipTextEncoder.cs
  + YoloWorld/ExternalLlmClipTextEncoder.cs
  + YoloWorld/ClipTokenizer.cs (self-contained BPE)
  + YoloWorld/YoloWorldOptions.cs
  + YoloE/OnnxYoloEDetector.cs (AGPL, swap-ready)

SafeView.LLM:
  + OpenAiCompatibleEmbeddingsClient.cs
  + EmbeddingsClientFactory.cs

SafeView.Web:
  + Endpoints/DetectionClassRefEndpoints.cs (path-traversal safe)
  + Components/Pages/DetectionClasses.razor + DetectionClassDialog + DetectionClassStatsDialog
  + Components/Pages/PromptPacks.razor + PromptPackCompileDialog + PromptPackCompileRequest

scripts:
  + download-models.sh — targets: yolo-world-v2-s (Apache 2.0), yoloe-11s (AGPL + interactive warning)
```

---

## Gdy pracujesz w obszarze X — czytaj najpierw

- **Pipeline**: `src/SafeView.Application/Detection/DetectionPipeline.cs` + `documentation/DETECTION_PIPELINE.md`
- **Homografia**: `src/SafeView.Domain/Cameras/HomographyCalculator.cs` + `tests/SafeView.Domain.Tests/Cameras/HomographyTests.cs`
- **Spatial filters**: `src/SafeView.Application/Detection/SpatialFilterEvaluator.cs`
- **VLLM schema**: `src/SafeView.Infrastructure/Vllm/BuiltInTemplates.cs` (CommonSchema + CommonSystemRules)
- **VLLM generator**: `src/SafeView.LLM/LlmPromptGenerator.cs` (MetaPrompt + RefineAsync)
- **Multi-provider LLM**: `src/SafeView.LLM/ChatClientFactory.cs` + `src/SafeView.Domain/Llm/LlmProvider.cs`
- **Monitor multi-tile**: `src/SafeView.Web/wwwroot/monitor.js` (session-token per-tile, abort fetch)
- **Flow topology**: `src/SafeView.Web/wwwroot/flow.js` + `src/SafeView.Web/Hubs/FlowHub.cs`
- **Theme chipów**: `src/SafeView.Web/wwwroot/theme-light.css` sekcja "CHIPS" (per-color outlined overrides)
- **DetectionClass library**: `src/SafeView.Domain/Detection/DetectionClass.cs` + `src/SafeView.Infrastructure/Detection/BuiltInDetectionClasses.cs`
- **Trigger matcher** (legacy + class-based): `src/SafeView.Application/Detection/TriggerConditionMatcher.cs`
- **YOLO-World inference**: `src/SafeView.ML/YoloWorld/OnnxYoloWorldDetector.cs` + `OnnxClipTextEncoder.cs` + `ClipTokenizer.cs`
- **Detector capability gate**: `src/SafeView.ML/DetectorFactory.cs` — plug-in resolve po backend name
- **YOLOE (swap target)**: `src/SafeView.ML/YoloE/OnnxYoloEDetector.cs` — 3 interfejsy, visual embeddings cache
- **Compiled prompt packs**: `src/SafeView.Application/Detection/PromptPackCompiler.cs` + `src/SafeView.Domain/Detection/CompiledPromptPack.cs`

---

Następne zadania → **`ROADMAP.md`**.
