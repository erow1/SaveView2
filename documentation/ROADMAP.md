# SafeView — Roadmap (ticket-style)

**Jak używać**: każdy ticket jest **self-contained** — możesz w nowej sesji Claude napisać *„zrób ticket #N z ROADMAP"* i Claude ma wszystko czego potrzebuje bez czytania historii.

Priorytet: **Critical**, **Important**, **Nice-to-have**.

---

## Ukończone (2026-04-20 → 2026-04-22)

### ✅ TICKET #1 — Szyfrowanie sekretów `Action.Config` (AES-GCM)
`ISecretCipher` + klucz w `storage/licenses/secrets.key`. Format `enc:v1:{base64}`. Backward-compat plaintext. 15 testów zielone.

### ✅ TICKET #2 — GPU Acceleration (opcjonalne)
`MLOptions.UseGpu/GpuDeviceId/FallbackProvider`. Chain: CUDA → DirectML/CoreML → CPU. User dodaje `Microsoft.ML.OnnxRuntime.Gpu` ręcznie (1.5GB NuGet).

### ✅ TICKET #3 — In-app notifications (runtime broker)
`IInAppNotificationBroker` singleton → `MainLayout.Snackbar.Add` u każdego aktywnie zalogowanego usera.

### ✅ TICKET #4 — Test-fire buttons (Actions/Triggers)
`IActionDispatcher.TestFireAsync` (pomija gate Enabled + rate-limit, audit loguje). UI przyciski Play.

### ✅ TICKET #5 — Live monitor (`/monitor`)
Video wall kafli, multi-tile poller (monitor.js), SVG overlay + HTML labels, switch ROI/Zones/Detections, sync frame.

### ✅ TICKET #6 — Flow topology (`/flow`)
Cytoscape.js + SignalR FlowHub live events. Deep-link ?edit=id&returnTo=/flow do CRUD.

### ✅ TICKET #7 — Kamera: kalibracja homografii
`/cameras/{id}/calibration` z dwoma trybami (Prostokąt / Punkty). `HomographyCalculator` DLT. 5 testów.

### ✅ TICKET #8 — Spatial filters w Triggerze (dystans w metrach)
`SpatialFilter.PairDistance` (ModelA/B + labels + min/max m + AnyPair/AllPairs). Pipeline gating.

### ✅ TICKET #9 — VLLM Fazy A-D (pełna pętla jakości)
- Biblioteka 12 built-in szablonów BHP z strict schema (observations/alternatives/severity/quality/action)
- Strony: `/admin/vllm-templates`, `/admin/vllm-playground`
- Generator z opisu + Refine z feedbackiem (LlmPromptGenerator)
- Statystyki per-szablon (FP rate, histogram confidence, avg) + drill-down z MudChart
- Auto-versioning szablonów (override UpdateAsync) + historia + rollback
- Import/Export JSON
- Triple gate w VllmChecker (confidence + severity + quality)
- Feedback przez incident → `WasFalsePositive` flag

### ✅ TICKET #10 — Multi-provider LLM
`LlmProvider` entity + `IChatClientFactory` z cache. Presety per typ (Ollama/LMStudio/vLLM/OpenAI/Azure/Groq/Together/Mistral/DeepSeek/LocalAI). Dropdown w TriggerDialog + Playground.

### ✅ TICKET #11 — Incident workflow clean-up
Klikalne wiersze → `/incidents/{id}`. Pasek akcji na karcie (Analiza/Potwierdź/Rozwiąż/Fałszywy alarm — zunifikowane). Theme-aware chipy (MudBlazor Color + theme-light.css overrides).

### ✅ TICKET #12 — Menu 3-sekcyjne
Operator / Konfiguracja / Administracja (MudNavGroup).

### ✅ TICKET #13 — Sampler adaptacyjny
Usunięty globalny CameraRefreshSeconds — per-camera kadencja, floor MinTickMilliseconds.

---

## Open-Vocabulary Detection refactor (2026-04-23, 7 faz)

### ✅ TICKET #31 — Faza 1: DetectionClass fundament
`DetectionClass` entity (Kind: ClosedSetBinding / Text / Visual / TextAndVisual), `ModelCapabilities` [Flags], `TriggerCondition.DetectionClassId?` backward-compat, `TriggerConditionMatcher` wspólny, 14 built-in klas BHP, permisja. 23 nowe testy.

### ✅ TICKET #32 — Faza 2: Interfejsy detektorów (plug-in pattern)
`ITextPromptDetector`, `IVisualPromptDetector`, `VisualPromptClass`. `IDetectorFactory` rozszerzony o capability-gate + backend resolve po `IObjectDetector.Backend` string. Dodanie nowego backendu = 4 kroki (enum + impl + DI + switch). 7 testów.

### ✅ TICKET #33 — Faza 3: OnnxYoloWorldDetector (Apache 2.0)
Pełna implementacja: auto-detect dynamic/compiled, batch (SAHI), `IClipTextEncoder` z hot-swap 2 implementacji (InProcessOnnx / ExternalLlm via `IEmbeddingsClientFactory`), self-contained `ClipTokenizer` (BPE). `download-models.sh` target `yolo-world-v2-s`. `ModelSeeder` auto-rozpoznaje. 6 testów + conditional-skip integration.

### ✅ TICKET #34 — Faza 4: UI biblioteki klas + TriggerDialog refactor
`/admin/detection-classes` z groupowaniem kategorii, DetectionClassDialog per-kind, TriggerDialog switch "użyj klasy z biblioteki" (class-based path) vs legacy ModelId+Labels. Nav menu + resx PL/EN.

### ✅ TICKET #35 — Faza 5: Compiled prompt packs (persistent cache)
`CompiledPromptPack` entity + repo + `PromptPackCompiler`. Detektor pre-check cache przed CLIP encode → skip ~10-30ms per klatka. `/admin/prompt-packs` UI z multi-select compile + stale detection. Decyzja: **nie robimy re-param ONNX grafu** (wymagałby Python sidecar / OnnxSharp) — cache daje identyczny gain. 9 testów.

### ✅ TICKET #36 — Faza 6: Visual-prompt backend (swap-ready)
`FileKind.DetectionClassRef` + 3 endpointy (path-traversal safe, Guid server-side). UI wizard upload/delete w DetectionClassDialog (IJSRuntime + fetch FormData). `OnnxYoloEDetector` (AGPL, swap target) — 2 sessions + per-class embedding cache. `download-models.sh yoloe-11s` z interaktywnym AGPL warningiem. Architektura udokumentowana pod swap na OWLv2 (Apache 2.0) w przyszłości.

### ✅ TICKET #37 — Faza 7: Quality loop per DetectionClass
`Incident` +: `DetectionClassId?` + `DetectionConfidence?` + index. Pipeline wypełnia przy CreateIncidentAsync. `GetStatsByDetectionClassAsync` + `DetectionClassStats` (FP rate / avg / histogram 10 bucketów). UI kolumna "Ostatnie 30 dni" + `DetectionClassStatsDialog` z MudChart + selektor okna 7/30/90 dni. 5 testów.

**Faza Open-Vocab suma**: 7 ticketów, +52 testy (łącznie 204/204 zielone), 0 warnings, architektura swap-ready dla YOLOE → OWLv2.

---

## Ukończone (2026-04-27)

### ✅ TICKET #53 — YOLO-World text-prompt fix (closed-set bug)

**Bug**: na `/models` test detect, niezależnie od user prompts, model zawsze wykrywał osoby z różnymi labelami.

**Root cause**: `runtime/models/yolo-world-v2-s/model.onnx` był produkowany przez `ultralytics yolo export model=yolov8s-worldv2.pt format=onnx`, co eksportuje **closed-set** model z zamrożonym vocab COCO (1 input, brak text path). Detektor cicho szedł do compiled path (skip CLIP encode), wracał z 80 wbudowanymi class scores, a `Postprocess` relabel-ował przez `prompts[s.cls]` — czyli osoby (COCO class 0) dostawały label = pierwszy user prompt. CLIP `text-encoder.onnx` w ogóle nie został pobrany (skrypt cicho przeszedł `2>/dev/null`).

**Fix (3 niezależne kawałki)**:

1. **Defensive guard w detector** — `OnnxYoloWorldDetector.DetectWithPromptsAsync` sprawdza `isDynamic && prompts.SequenceEqual(model.Labels)`. Gdy compiled + custom prompts → `DetectionResult.Failed("Model jest skompilowany z zamrożonym vocabulary...")`. Eliminuje silent lying.

2. **Postprocess refactor** — autodetect output format: `outputs.Count >= 2` → split (scores [B,N,nc] + boxes [B,N,4] xyxy), inaczej fused YOLOv8 [B,4+nc,N]. Nowa metoda `PostprocessSplit` + `PostprocessSplitBatchItem` + helper `IoUXyxy`.

3. **UI hint** — `ModelTestDetectEndpoint` zwraca `warning` field gdy fallback do default flow ignoruje user prompts. `ModelTestDialog.razor` renderuje MudAlert (warning gdy text ignored, error gdy detector zwrócił business-error).

**Replacement model**: `runtime/models/yolo-world-v2-s/`:
- `model.onnx` ← `jquadrino/yolo-world-onnx` (HF, MIT, ~400 MB FP32) — 2 inputs (image + text_features [B,classes,512]), 2 outputs (scores [B,8400,classes] sigmoid + boxes [B,8400,4] xyxy w 640×640)
- `text-encoder.onnx` ← `Xenova/clip-vit-base-patch32` (~242 MB) — CLIP ViT-B/32 text, embed dim 512
- `tokenizer/vocab.json` + `merges.txt` ← Xenova clip BPE

**Download script**: `scripts/download-models.sh` całkowicie przepisany dla yolo-world-v2-s (dropped ultralytics path, jedyna ścieżka HF).

**Tests**: 209/209 zielone (poprzednio 204 — +5 ApiCamera tests w międzyczasie). Build 0/0.

---

## Ukończone (2026-04-26)

### ✅ TICKET #52 — Swagger / OpenAPI (`Swashbuckle.AspNetCore` 7.2.0)

**Kontekst**: po Ticketach #43-#49 mamy `/api/v1/*` REST API + ingest, ale brakowało interactive docs. Devs/integratorzy musieli czytać `documentation/API_INGEST.md` + grep źródeł.

**Goal**: Swagger UI pod `/swagger` z security schema dla API key, automatyczne grupowanie po tag-ach, "try it out" w przeglądarce.

**Dostarczone**:
- `Swashbuckle.AspNetCore` 7.2.0 do `Directory.Packages.props` (offline — assets bundled w NuGet, nie wymaga vendoringu).
- Swagger config w `Program.cs`: `SwaggerDoc("v1", ...)`, security scheme `ApiKey` (Bearer header), global security requirement, `DocInclusionPredicate` filtrujący do `/api/v1/*`.
- Swagger UI middleware z **cookie-auth gate** — anon redirect na `/login?ReturnUrl=/swagger`. Spec JSON pod `/swagger/v1/swagger.json`.
- Endpoint metadata na wszystkich `/api/v1/*` (`.WithTags/.WithSummary/.WithDescription/.Produces/.ProducesProblem`):
  - **ApiV1Endpoints**: Incidents (list + get by id), Cameras (list), Zones (list filtrowana), Health (`/ping`).
  - **IngestEndpoints**: multipart (`/ingest`) + JSON-only (`/ingest-json`) z dokładnymi opisami body, security, rate limit.
- Nav menu link: Administracja → "API docs (Swagger)" — otwiera w nowej karcie.
- resx PL/EN/neutral.

**Convention dla nowych endpointów**: po `.RequireAuthorization(...)` dorzuć `.WithTags("Group").WithSummary("Krótko").Produces<T>(200).ProducesProblem(401)`. CLAUDE.md ma sekcję "Swagger / OpenAPI" z tym hint-em.

**Tests**: build 0/0, runtime weryfikacja po `sudo ./restart.sh`: `curl /swagger/v1/swagger.json` (z cookie session) zwraca OpenAPI 3.0 JSON, UI ładuje się pod `/swagger`.

---

### ✅ TICKET #43-#49 — ApiCamera (push-based ingest)

**Problem**: dotychczas SafeView pollował kamery przez RTSP/HTTP/File, ale wdrożenia z edge inference (Frigate, własny ML server, embedded camera z built-in detekcją) chciały pchać klatki + pre-computed detekcje, nie być pollowane.

**Rozwiązanie**: nowy `CameraTransport.Api` + REST ingest endpoint. Zewnętrzny system POST-uje klatkę + JSON z detekcjami; pipeline pomija inferencję i wpada do tego samego trigger/VLLM/action flow co normalne kamery. Downstream nie wie że źródło zewnętrzne.

**Dostarczone (#43-#49)**:
- **#43 Domain**: `CameraTransport.Api`, `CameraVendor.ApiPush=100`, `Roi.IsFullFrame`, `Camera.IngestApiKeyId?`, `Permission.ApiCamerasWrite`.
- **#44 Pipeline**: `IDetectionPipeline.ProcessExternalDetectionsAsync(...)` + extract wspólnej `EvaluateAndDispatchAsync` (snapshot+repos+homography+eval+VLLM+actions+audit).
- **#45 Web ingest**: `IngestEndpoints` (multipart + JSON-only), `IngestDtos` (schema 1.0 zbliżona do Roboflow), `IngestIdempotencyStore` (LRU 1000/cam), DI + rate limit `camera-ingest` (1800 req/min/cam).
- **#46 Auto-provisioning**: `IApiCameraProvisioner` + `ApiCameraProvisioner` — pełnokadrowa ROI `(0,0,1,1)` + Zone (4-punkt polygon) tworzone idempotentnie przy save kamery typu Api. Wywoływane z `Cameras.razor`. `CameraFrameSampler` skipuje `Transport.Api`.
- **#47 UI**: CameraDialog Vendor=ApiPush → panel z URL endpointu + 2 expansion panels curl examples + `IngestApiKeyId` pin field + warning gdy IsNew. resx PL/EN/neutral.
- **#48 Hardening**: rate limit per-camera done. Frame retention TTL → follow-up #50.
- **#49 Doc**: `documentation/API_INGEST.md` — pełna spec, curl examples, error codes, smoke test E2E.

**Tests**: 5 nowych `ApiCameraProvisionerTests` → łącznie 209/209 zielone, 0 warnings.

**Format API** (kluczowe decyzje):
- Bbox **pixel coords** (top-left origin) — server normalizuje do `[0..1]`. Pasuje do output YOLO.
- `ModelId="external"` constant — używaj DetectionClass-based conditions w triggerach, nie ModelId+Labels.
- Idempotency po `frame_id` (sender UUID) — retry safe.
- `source.name/model/inference_ms` + `attributes` (wolny dict) dla audytu i forward-compat.
- Schema versioned: URL `/api/v1/` + body `schema_version` field.
- JSON-only fallback (`image_base64` lub `image_url`) dla cloud webhooków.

---

### ✅ TICKET #42 — LLM config consolidation (single source of truth)

**Problem**: trzy źródła konfiguracji LLM rozjeżdżały się: strona `/admin/llm` (read-only display z appsettings), strona `/admin/llm-providers` (DB-backed), sekcja `appsettings.Llm`. Edycja providera w UI pokazywała inne dane niż `/admin/llm`. Dwa codepathy konsumentów: bezpośredni `IChatClient` (z appsettings) vs `IChatClientFactory.GetForAsync` (z DB).

**Rozwiązanie**: jedyne źródło prawdy = `LlmProvider` w Mongo, edytowane przez `/admin/llm-providers`.

- **Konsumenci po refactorze** — wszyscy przez `IChatClientFactory`: `Assistant.razor`, `CameraDetail.razor`, `IncidentAnalyzer`, `LlmPromptGenerator`, `VllmChecker` (drop legacy `_defaultChat`).
- **Factories** (`ChatClientFactory`, `EmbeddingsClientFactory`) — usunięty fallback do `IOptions<LlmOptions>`. Brak providera = `InvalidOperationException` z linkiem do strony konfiguracji.
- **Skasowane**:
  - `Components/Pages/LlmAdmin.razor` (read-only display strony)
  - `src/SafeView.Infrastructure/Llm/LlmProviderSeeder.cs` (one-shot migrator z appsettings → DB)
  - DI: `services.AddOptions<LlmOptions>().Bind(...)`, `services.AddHttpClient<IChatClient, OpenAiCompatibleChatClient>()`, `services.AddHostedService<LlmProviderSeeder>()`
  - `appsettings.json` sekcja `"Llm"` + `appsettings.Runtime.json` `"Llm": {}`
  - `LlmOptions.SectionName` const
  - resx: `Nav.Llm`, `LlmAdmin.DefaultModel` (3 pliki PL/EN/neutral)
  - Menu link `/admin/llm` w `NavMenu.razor`
- **Pozostawione**:
  - `LlmOptions` jako wewnętrzny DTO budowany przez factory z `LlmProvider` (zaktualizowany docstring). Konsumują go ctory `OpenAiCompatibleChatClient` i `OpenAiCompatibleEmbeddingsClient`.
  - `AddSafeViewLLM(IServiceCollection)` — dropped `IConfiguration` parameter, `Program.cs` zaktualizowany.
- **UX**: `Assistant.razor` + `CameraDetail.razor.SummarizeAsync` mają empty-state alert "Brak skonfigurowanego dostawcy LLM" + przycisk "Otwórz Dostawców LLM" → `/admin/llm-providers`.
- **Migracja**: BRAK (decyzja: nie produkcja jeszcze; user musi raz przejść na `/admin/llm-providers` po starcie).

**Tests**: 204/204 zielone, 0 warnings.

**Files changed**: 14 (zmodyfikowane) + 2 (usunięte: `LlmAdmin.razor`, `LlmProviderSeeder.cs`).

---

## Otwarte tickety

## 🔴 TICKET #14 — Szyfrowanie API key w `LlmProvider` (Critical)

**Kontekst**: `LlmProvider.ApiKey` (chmurowe providery: OpenAI, Groq itd.) zapisywany plain text w Mongo. Utrata bazy = wyciek kluczy.

**Goal**: re-use `ISecretCipher` (z Ticketu #1) dla `LlmProvider.ApiKey`.

**Approach**:
1. `MongoLlmProviderRepository` override Insert/Update — encrypt ApiKey gdy non-null.
2. Przy Get/List — decrypt.
3. `SecretsPolicy.SensitiveConfigKeys` już ma `"api_key"` — reuse logiki.

**Tests**: `LlmProvider_ApiKey_EncryptedOnInsert`, `LlmProvider_ApiKey_DecryptedOnLoad`, backward-compat plaintext.

**Est**: 2-3h

---

## 🟡 TICKET #15 — Wielokrotne providery per trigger (cascade chat)

**Kontekst**: user może chcieć fallback gdy primary LLM (vLLM) nie odpowiada → secondary (Ollama lokalnie). Albo: użyj taniego dla pre-screen, drogiego dla krytycznych.

**Goal**: `VllmCheckConfig.FallbackProviderId?` + w `VllmChecker` gdy primary zwróci `IsError` i `!RejectOnError` → retry z fallback.

**Approach**:
1. `VllmCheckConfig` + pole `FallbackProviderId?`.
2. `VllmChecker.CheckAsync` — po LLM error sprawdź fallback, retry raz.
3. UI: drugi dropdown "Fallback provider" w sekcji VLLM trigger dialogu.
4. Audit: w `VllmCheckResult.RawResponse` zaznacz że użyto fallback.

**Est**: 4h

---

## 🟡 TICKET #16 — Regeneracja prompt z analizą FP rate

**Kontekst**: Generator/Refine bierze feedback od usera. A co jeśli LLM dostałby automatycznie **konkretne przykłady fałszywych alarmów** z historii tego szablonu (ostatnie N incidentów z `WasFalsePositive=true`)?

**Goal**: Przycisk "Auto-refine" w `VllmTemplateStatsDialog` — LLM dostaje: aktualny prompt + 5 najnowszych FP (frame + observations + reason) + prośbę "popraw żeby te przypadki nie były confirmed".

**Approach**:
1. Rozszerz `IPromptGenerator` o `AutoRefineAsync(template, recentFalsePositives)`.
2. Meta-prompt dostaje przykłady `{reason, observations}` z realnych FP.
3. Przycisk "Auto-popraw na podstawie FP" w `VllmTemplateStatsDialog` (widoczny gdy FP count >= 3).

**Est**: 5h

---

## 🟡 TICKET #17 — Anthropic Claude / Gemini

**Kontekst**: Obecnie wszystkie providery są OpenAI-compat. Anthropic ma inny format (`content` jako array, `system` poza messages). Google Gemini podobnie. Brak — niektóre modele są silniejsze w vision.

**Goal**: Dedykowane klienty + rozszerzenie `LlmProviderKind` + `IChatClientFactory` wybiera odpowiedni client per kind.

**Approach**:
1. `AnthropicChatClient : IChatClient` — Claude API (`/v1/messages`, `x-api-key` header zamiast Bearer).
2. `GeminiChatClient : IChatClient` — Google Vertex / Generative AI.
3. `ChatClientFactory` switch per `LlmProviderKind`.
4. UI presety.

**Est**: 8-10h (per client)

---

## 🟡 TICKET #18 — Eksport incidentów jako ZIP (klatka + JSON)

**Kontekst**: Audit / compliance — operator bhp chce zabrać incydenty z filtra (np. wszystkie z ostatniego miesiąca, PPE violations) jako ZIP z klatkami + metadata.

**Goal**: `GET /api/v1/incidents/export?from=X&until=Y&category=Z` → ZIP z `metadata.json` + `frames/*.jpg`.

**Approach**:
1. `IReportService.ExportIncidentsAsZipAsync(filter) → Stream`.
2. Endpoint w `ApiV1Endpoints` z policy `reports:generate`.
3. UI: przycisk "Eksportuj (ZIP)" na `/incidents` z filtrami.

**Est**: 4-5h

---

## 🟡 TICKET #19 — Tracking obiektów (ByteTrack/SORT) + dynamika

**Kontekst**: Spatial filter obecnie działa na stanie chwilowym (per-klatka). Nie możemy wykryć "kierowca oddalający się od pojazdu" (wymaga śledzenia pozycji w czasie).

**Goal**: Lekki tracker (ByteTrack lub prostszy IoU-based) który utrzymuje `TrackId` między klatkami + nowy typ filtra "velocity/direction".

**Approach**:
1. `ITracker` + `SimpleIouTracker` — matchuje detekcje klatka-do-klatki przez IoU > 0.3.
2. `DetectionResult.TrackId?` po trackerze.
3. `DetectionSnapshotStore` zachowuje historie per-track (deque ostatnich N pozycji).
4. Nowy `SpatialFilterKind.Velocity` + `SpatialFilterKind.Direction`.

**Est**: 15-20h (big ticket)

---

## 🟡 TICKET #20 — Real-time push detekcji do `/monitor` przez SignalR

**Kontekst**: Obecnie `/monitor` poll-uje `/api/monitor/{id}` co N ms. Przy 10 kamerach × 4 ms interval = 40 requests/sec. Zamiast tego push z `DetectionPipeline.CreateIncidentAsync` przez SignalR.

**Goal**: Dodaj do `FlowHub` eventu `DetectionUpdate(cameraId, detections, homography)`. `monitor.js` subskrybuje zamiast pollować `/api/monitor`.

**Approach**:
1. Pipeline → `IFlowEventPublisher.DetectionsUpdatedAsync(cameraId, snapshot)`.
2. Poller nadal potrzebny dla obrazu (JPEG nie przez SignalR sensownie), ale dane detekcji lecą push.

**Est**: 5-6h

---

## 🟢 TICKET #21 — bUnit testy Blazor (top 5 dialogów)

Bez zmian vs poprzednia wersja roadmap. Target: Login/ZoneEditor/TriggerDialog/ActionDialog/Zones. **Est**: 8-10h

---

## 🟢 TICKET #22 — E2E testy DetectionPipeline z realnym modelem ONNX

Bez zmian. **Est**: 8h

---

## 🟢 TICKET #23 — Docker Compose deploy (web + mongo)

**Kontekst**: Obecnie `sudo ./start.sh` bare-metal jako root. Dla produkcji u klientów lepiej Docker.

**Goal**: `Dockerfile` (multi-stage: SDK→publish→runtime + apt ffmpeg + bundled mediamtx) + `docker-compose.yml` (web + mongo + volumes).

**Approach**:
1. Remapping portów (80:8080) — kontener listen 8080, nie-root.
2. Volumes: `storage/`, `runtime/models/` (persistent), `licenses` (persistent).
3. GPU: `--gpus all` w compose (opcjonalne, behind profile).
4. `documentation/DEPLOY.md` z instrukcją.

**Est**: 6h

---

## 🟢 TICKET #24 — Backup / restore scripts

**Kontekst**: Mongo backup + license + runtime config jako ZIP. Cron / systemd timer.

**Goal**: `scripts/backup.sh` + `scripts/restore.sh` + dokumentacja `BACKUP.md`.

**Est**: 3h

---

## 🟢 TICKET #25 — Relay Action Handler (Modbus TCP)

Bez zmian. `ActionType.Relay = 12` istnieje. **Est**: 5-6h

---

## 🟢 TICKET #26 — SMS Action Handler (Twilio/SMSAPI)

Bez zmian. `ActionType.Sms = 13`. **Est**: 4h

---

## 🟢 TICKET #27 — Chart.js/MudChart dashboards wydajności

Timeline per camera/model (ostatnia 1h, bucket 30s). **Est**: 5-6h

---

## 🟢 TICKET #28 — Prometheus exporter

`/metrics` endpoint z safeview_pipeline_latency_ms (histogram per camera), safeview_detections_total, itd. **Est**: 4-5h

---

## 🟢 TICKET #29 — Incident TTL / retention policy

Obecnie `incidents` collection rośnie bezterminowo. Dodać configurable retention (np. 180d) + opcjonalny TTL index.

**Est**: 2h

---

## 🟡 TICKET #38 — Pipeline integration dla visual-prompt klas

**Kontekst**: Po Fazie 6 mamy `OnnxYoloEDetector : IVisualPromptDetector` + wizard uploadu refs + storage endpointów. Ale `DetectionPipeline.RunModelsForRoiAsync` używa tylko `IObjectDetector` / `ITextPromptDetector` — nie wywołuje `DetectWithVisualPromptsAsync` gdy trigger.DetectionClass.Kind == Visual.

**Goal**: Pipeline rozpoznaje Visual kind i routuje do visual-prompt detector z pre-resolved `VisualPromptClass` (classId + label + abs paths do refs).

**Approach**:
1. `DetectionPipeline.RunModelsForRoiAsync` — gdy trigger ma klasy Visual w conditions, resolve refs przez `IFileStore.ResolveAbsolutePath(FileKind.DetectionClassRef, r.RelativePath)` i buduj `VisualPromptClass`.
2. Wywołaj `IDetectorFactory.GetVisualPromptDetector(model)` i `DetectWithVisualPromptsAsync`.
3. Detekcje mergowane z istniejącymi text-prompt detekcjami w tej samej ROI.
4. Gdy klasa Text+Visual — decyzja: używaj kombo (gdy model wspiera) albo per-kind dispatch.

**Est**: 3-4h

---

## 🟡 TICKET #39 — Auto-invalidation embeddings cache przy edycji klasy

**Kontekst**: Po edycji `DetectionClass.TextPrompt` albo upload/delete `VisualReferences`, cache embeddingów w `OnnxYoloWorldDetector` / `OnnxYoloEDetector` nie jest invalidowany automatycznie. `CompiledPromptPack` nie jest oznaczany jako stale — dopiero gdy user ręcznie kliknie "Refresh staleness".

**Goal**: Event-driven invalidation po stronie repo.

**Approach**:
1. `MongoDetectionClassRepository.UpdateAsync` override — po zapisie emit event `IDetectionClassInvalidator.InvalidateAsync(classId)`.
2. `IDetectionClassInvalidator` (Application) — wstrzykiwany w detektorach; `InvalidateClassCache` + `MarkPacksStale`.
3. Analogicznie dla `DetectionClassRefEndpoints` (POST/DELETE) — po modyfikacji visual refs.

**Est**: 2-3h

---

## 🟡 TICKET #40 — Integration test YOLOE (conditional-skip)

**Kontekst**: Mamy `YoloWorldIntegrationTests` conditionally-skipped gdy brak bundled modelu. Analogiczny brakuje dla YOLOE — end-to-end flow tokenize + text encode + visual encode + detect nie jest zweryfikowany automatem nawet gdy user ma pobrany model.

**Goal**: `YoloEIntegrationTests` — 2-3 testy (text-only, visual-only, text+visual combo).

**Approach**: pattern 1:1 z `YoloWorldIntegrationTests`. Auto-skip gdy `runtime/models/yoloe-11s/` brak.

**Est**: 1-2h

---

## 🟡 TICKET #41 — OWLv2 swap (Apache 2.0 zamiast AGPL YOLOE)

**Kontekst**: Obecnie `IVisualPromptDetector` ma jedną implementację (`OnnxYoloEDetector`, AGPL). Dla wdrożeń komercyjnych bez Ultralytics Enterprise License wymagany swap. OWLv2 (Google Research, Apache 2.0) jest architektonicznym swap target — wszystko już przygotowane pod plug-in.

**Goal**: `OnnxOwlV2Detector` zamienia YOLOE bez breaking change.

**Approach**:
1. `DetectorBackend.OwlV2 = 4` (enum value).
2. `OnnxOwlV2Detector : IObjectDetector, ITextPromptDetector, IVisualPromptDetector` — HuggingFace Transformers ONNX export (model + processor config).
3. DI rejestracja w `SafeView.ML.DependencyInjection`.
4. `DetectorFactory` case `OwlV2 => ResolveVisualDetector("OwlV2", ...)`.
5. `download-models.sh` target `owlv2-base-patch16` (Apache 2.0, ~400MB).
6. `ModelSeeder` rozpoznanie folderu (specific files: `config.json`, `preprocessor_config.json` obok `model.onnx`).
7. Migracja klas Visual użytkownika — cache embeddings invalidate (inne przestrzenie vector).
8. Dokumentacja: `runtime/models/README.md` wskazówka "dla Apache 2.0 deployment use OwlV2 instead of YoloE".

**Uwaga architektoniczna**: Fazy 1-7 już zapewniają że ten ticket to "drop-in", nie refaktor — `DetectionClass`, `Trigger`, UI, pipeline bez zmian.

**Est**: 8-10h (największy ze względu na nową inferencję ViT architecture)

---

## 🟡 TICKET #50 — Frame retention TTL (hosted service)

**Kontekst**: po Ticketach #43-#49 mamy `CameraTransport.Api` które potrafi przyjmować 1800 req/min/cam. Bez retention dysk się zapełni: 30fps × 30 dni × 4K JPG ≈ 250GB/cam. Dla kamer RTSP też nie ma TTL.

**Goal**: `FrameRetentionService` (IHostedService) skanuje `storage/frames/` co N godzin i usuwa pliki starsze niż `Storage:FrameRetentionDays` (configurable, default 30d).

**Approach**:
1. `StorageOptions.FrameRetentionDays` (int, default 30; 0 = disable).
2. `FrameRetentionService : IHostedService` w SafeView.Infrastructure. `Timer` co 6h. Iteruje `runtime/storage/frames/` (przez `IFileStore.ResolveAbsolutePath(FileKind.Frame, "")`) → enumeruje pliki → `File.GetLastWriteTimeUtc < now - retention` → delete.
3. Logging: ile usunięto + bytes freed (per run, do `system_events` Info).
4. Test jednostkowy z `IFileStore` mock.

**Est**: 2-3h

---

## 🟡 TICKET #51 — Persistent ingest idempotency (Mongo TTL)

**Kontekst**: `IngestIdempotencyStore` (#45) jest in-memory LRU 1000 frame-ów per kamera. Restart aplikacji = utrata stanu, multi-instance deploy = niezsynchronizowane.

**Goal**: Mongo collection `ingest_idempotency` z TTL index (24h), composite key `(cameraId, frameId)`.

**Approach**:
1. `IIngestIdempotencyStore` przeniesione do Application/Abstractions.
2. `MongoIngestIdempotencyStore` w Infrastructure — `InsertOneAsync` z `unique` index na `(cameraId, frameId)`. `MongoWriteException` z duplicate key = "already_processed".
3. TTL index na `CreatedAt` z `expireAfterSeconds: 86400`.
4. Migration: w DI rejestrujemy nowy impl, drop in-memory.

**Est**: 2-3h

---

## 🟢 TICKET #30 — Ostrzeżenia gdy cascade kontrproduktywny

Obecnie cascade ma fixed threshold. Dodać auto-monitoring: gdy `MaxCandidatesForCascade` (np. 5+ kandydatów w klatce) → log Warning "cascade może być wolniejszy niż full ROI, rozważ wyłączenie".

**Est**: 2h

---

## Niewyspecyfikowane (ideas)

- HLS live streaming zamiast snapshots-as-video (WebRTC przez MediaMTX)
- Custom trainer UI — user trenuje YOLO na swoich zdjęciach bez opuszczania aplikacji
- Mobile app — notyfikacje push iOS/Android
- Federacja — wiele instancji SafeView sync przez central
- Incident graph view — powiązane incydenty jako graf (jak flow)
- Multi-camera grid live (WebRTC) dla ścian monitorów bezpieczeństwa

---

## Konwencja na dalsze tickety

Gdy dodajesz nowy ticket:

1. **Numer sekwencyjny** (następny wolny)
2. **Priorytet** (Critical / Important / Nice-to-have)
3. **Kontekst** — 2-3 zdania dlaczego to jest potrzebne
4. **Goal** — jedno zdanie co ma być osiągnięte
5. **Files to modify / create** — konkretne ścieżki
6. **Approach** — kroki implementacji (wystarczy dla mid-level dev)
7. **Tests** — co zweryfikujemy
8. **Est** — godziny (1-8h = small; >8h = podziel)

Tickety **self-contained**: Claude w nowej sesji powinien móc zrobić ticket bez czytania innych. Jeśli ticket wymaga kontekstu → link do `PROJECT_STATE.md` / `CLAUDE.md` / konkretnego pliku źródłowego.
