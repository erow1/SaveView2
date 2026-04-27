# SafeView — playbook dla Claude Code

**Ten plik jest automatycznie czytany przez Claude Code przy każdej sesji.** Zawiera architekturę, konwencje i nieoczywiste decyzje — żeby nie tracić kontekstu przy przełączaniu sesji.

Rozszerzający stan projektu: `documentation/PROJECT_STATE.md`. Następne zadania do zrobienia: `documentation/ROADMAP.md`.

---

## Cel aplikacji

SafeView — enterprise computer-vision platforma dla bezpieczeństwa przemysłowego (BHP). Analizuje klatki z kamer IP (głównie 4K) używając modeli YOLO, wykrywa naruszenia (brak hełmu, ogień, intruzi, niebezpieczne zachowania), eskaluje jako incydenty + notyfikacje + akcje zewnętrzne (email, webhook, Modbus relay). Opcjonalnie waliduje detekcje przez VLLM (vision LLM) przed odpalaniem akcji.

## Stack technologiczny

- **.NET 10** (C# 13, `Lock`, primary constructors)
- **Blazor Server** + **MudBlazor 8** (UI)
- **MongoDB** (persistence — przez MongoDB.Driver)
- **Serilog** (logging — konsola + plik + custom MongoDB sink)
- **ONNX Runtime** (ML inference — bundled BHP models out-of-box: `yolov8n/s-coco` general (Apache 2.0), `yolov8n-ppe` PPE detection 10 klas (MIT, Hansung-Cho), `yolov8s-fire-smoke` (Mehedi-2-96), **`owlv2-base/large` open-vocab text-prompt** (Apache 2.0), YOLOE AGPL text+visual swap-ready, CLIP ViT-B/32 text+image encoders). YOLO-World v2 **usunięty 2026-04-27** (matchowanie wizualnie podobnych fragmentów zamiast klas).
- **SixLabors.ImageSharp** (image processing)
- **xUnit + FluentAssertions + NSubstitute** (testy)
- **OpenAI-compatible API** (LLM — vLLM, Ollama, OpenAI, LM Studio; chat + embeddings endpoints)
- **MediaMTX** (RTSP/HLS gateway — bundled binary)
- **FFmpeg** (snapshots, clips — bundled binary)

## Architektura warstwowa (DDD)

```
src/
  SafeView.Domain/            # Czyste encje, zero zależności zewnętrznych
  SafeView.Application/       # Abstractions + use cases + pipeline (Application.Detection)
  SafeView.Infrastructure/    # Mongo, FileStore, Serilog sinks, hosted services
  SafeView.Security/          # Authn/Authz, policies, seeding
  SafeView.Licensing/         # AES-GCM license validation
  SafeView.ML/                # ONNX detectors (classical + YoloWorld/ + YoloE/), SAHI sliced inference, ROI cropping, CLIP encoders, prompt pack compiler
  SafeView.Cameras/           # RTSP/HLS, MediaMTX manager, ffmpeg snapshot, bundled binaries
  SafeView.Roboflow/          # Roboflow cloud detector (alternative to ONNX)
  SafeView.LLM/               # OpenAI-compatible chat client, VLLM checker, incident analyzer
  SafeView.Notifications/     # SMTP + webhook (legacy per-incident; Faza 4 ma osobne handlery)
  SafeView.Reports/           # Incident reports (CSV, HTML, PDF)
  SafeView.Web/               # Blazor pages, endpoints, SignalR hubs
tests/
  SafeView.Domain.Tests/      # 105 testów (geometria, bbox, scheduler, DetectionClass caps, domain invariants)
  SafeView.Application.Tests/ # 84 testów (TriggerEvaluator, matcher, stats, VllmChecker contract, PromptPackCompiler)
  SafeView.ML.Tests/          # 15 testów (DetectorFactory swap, ClipTokenizer BPE, YoloWorld integration conditional-skip)
  SafeView.Web.Tests/         # (puste)
runtime/
  binaries/{osx-arm64,linux-x64,win-x64}/  # ffmpeg + mediamtx (git-ignored, download-binaries.sh)
  models/{yolov8n-coco,yolov8s-coco,...}/  # ONNX models (git-ignored, download-models.sh)
documentation/
  DETECTION_PIPELINE.md       # Pełna spec Fazy 1-5 + SAHI + VLLM
  PROJECT_STATE.md            # Mapa stanu implementacji
  ROADMAP.md                  # Self-contained tickety na przyszłość
  flow_detection.pdf          # Diagram user requirements (Camera → ROI → Zone → ...)
scripts/
  download-binaries.sh        # Pobiera ffmpeg/mediamtx na 3 platformy
  download-models.sh          # Pobiera YOLO z Ultralytics + eksport ONNX z dynamic batch
start.sh / stop.sh / restart.sh  # Proces management (app działa JAKO ROOT!)
```

**Zasada zależności**: Domain → Application → Infrastructure/ML/Cameras/LLM → Web. Nigdy wyżej.

## NO EMOJI — ABSOLUTNY ZAKAZ

**Zero emoji w kodzie, UI, logach, komentarzach, dokumentacji, resx.** Aplikacja to narzędzie BHP dla enterprise — emoji są nieprofesjonalne, niespójne z designem i szkodzą czytelności w logach/terminalach.

- Nazwy w UI, labele przycisków, chipy statusów, menu — **bez emoji**. Używaj Material Icons (MudBlazor `<MudIcon Icon="@Icons.Material.Filled.X" />`).
- Logi (Serilog, ILogger) — **bez emoji** w message templates. Logi trafiają do plików i Mongo; emoji psują narzędzia.
- Komentarze w kodzie, docstring, README, PROJECT_STATE, ROADMAP, CLAUDE.md — **bez emoji**.
- Resx keys i wartości — **bez emoji**.
- Cytoscape labele i wszystkie JS rendery — **bez emoji**, używaj czystego tekstu + Material-style kolorów/kształtów.

Jedyne dopuszczalne wyjątki: **ikony Material Design** przez MudBlazor (`Icons.Material.*`) — to są SVG, nie Unicode emoji.

Jeśli widzisz emoji w istniejącym kodzie — usuwaj od ręki przy pierwszej okazji.

## OFFLINE-FIRST REQUIREMENT

**Docelowo aplikacja działa na produkcji bez dostępu do internetu.** Developerka ma internet, wdrożenie u klienta nie.

1. **Zero CDN w runtime** — żadnych `<script src="https://cdn..."` ani `<link href="https://fonts...">` w `wwwroot/` / `Components/`. Biblioteki JS/CSS idą do `src/SafeView.Web/wwwroot/vendor/` i są ładowane lokalnie.
2. **Piny wersji** trzymaj w `scripts/download-vendor.sh`. Skrypt pobiera raz, po klonie repo albo przy upgrade. Pliki w `wwwroot/vendor/` są commitowane (jak `runtime/models/` też docelowo są).
3. **Nowa JS-owa zależność** = update `scripts/download-vendor.sh` + run + commit pobranych plików. Nigdy inline CDN.
4. **Fonty**: system-ui albo JetBrains Mono z MudBlazor, albo bundluj do `wwwroot/fonts/`. Zakaz Google Fonts.
5. **Modele ML i binarki** mają analogiczne skrypty: `download-models.sh`, `download-binaries.sh`. Ten sam pattern.
6. **Sprawdzenie przed commitem**: `grep -r "https://" src/SafeView.Web/wwwroot/ src/SafeView.Web/Components/` — wyniki powinny być tylko w komentarzach / dokumentacji, nigdy `src=` / `href=` runtime assets.

Obecnie vendorowane: `cytoscape`, `dagre`, `cytoscape-dagre`, `@microsoft/signalr` (strona `/flow`).

## 🔴 GLOBAL SECURITY REQUIREMENT

**Aplikacja może być wystawiona do internetu.** Zasady:

1. **Każdy endpoint** musi mieć `.RequireAuthorization(...)` albo jawne `.AllowAnonymous()`.
2. **Każda strona Blazor** z `@page` musi mieć `@attribute [Authorize(Policy = "perm:...")]` albo `[AllowAnonymous]`.
3. **Global fallback policy** (skonfigurowana w `Program.cs`): każdy endpoint/strona bez jawnej decyzji wymaga uwierzytelnienia.
4. **Lista publicznych endpointów** (jawnie `.AllowAnonymous()` + rate-limit `login`):
   - `POST /auth/login`, `POST /auth/setup`, `POST /auth/logout`
   - `GET /health/live`, `GET /health/ready`
   - `GET /culture/set` (z allowlist kultur "pl"/"en")
   - Strony: `/login`, `/setup`, `/forbidden`, `/not-found`, `/Error`
5. **Nigdy nie wystawiać** `storage/` przez `UseStaticFiles()`. Dostęp tylko przez autoryzowane endpointy (np. `/cameras/{id}/snapshot.jpg`).
6. **Audit trail**: każda akcja użytkownika → `audit_log`, każdy techniczny błąd → `system_events` (TTL 7 dni), każdy trigger fire → `action_executions` (TTL 30 dni).

## Konwencje kodu

### Nazewnictwo
- **Namespace = folder** (standard .NET)
- **Domain entities** dziedziczą `SafeView.Domain.Common.Entity` (Id + CreatedAt + UpdatedAt)
- **Abstractions**: `I{Name}` w `Application/Abstractions/{Category}/`
- **Testy**: `Method_Scenario_ExpectedResult()` (konwencja xUnit). CA1707 wyłączony w testach.

### Persistence
- Każda encja ma swoje repo `I{Name}Repository` + `Mongo{Name}Repository`
- `MongoRepositoryBase<T>` daje CRUD za darmo
- **TTL indexes**: `action_executions` (30d), `system_events` (7d). Zakładane fire-and-forget w konstruktorze (inaczej blokuje start gdy Mongo wolne).

### DI
- Wszystkie singleton dla repozytoriów + handlerów (thread-safe)
- `IHostedService` dla background workers + seederów
- **Circular dependency warning**: `ISystemEventLogger` NIE może mieć `ILogger<T>` w konstruktorze (Serilog sink → LoggerFactory → cykl). Błędy w tych klasach logują przez `Console.Error.WriteLine`.

### Logging
- Use `ILogger<T>` (injected). `LogError`/`LogWarning`/`LogCritical` automatycznie trafiają do **MongoDB** przez `SystemEventSink` (Serilog) → widoczne w `/admin/system-events`.
- **Threshold**: konfigurowalny w `/admin/settings` → Diagnostics (domyślnie Warning+).
- W handlerach/sinkach — **NIE używać ILogger dla pętli** (rzuć przez `Console.Error` zamiast).

### Testy
- `dotnet test` w root = wszystkie projekty (132 testów, <200ms total)
- Mockowanie: **NSubstitute** (`Substitute.For<IInterface>()`)
- Assertions: **FluentAssertions** (`.Should().Be(...)`, `.Should().BeApproximately(...)`)
- IEEE-754: Używaj `BeApproximately(val, epsilon)` dla double'ów (0.2 + 0.4 ≠ 0.6 exactly)
- CA1859 fix: dla pól kolekcji używaj `Dictionary<>` / `List<>` zamiast `IReadOnlyDictionary<>` / `IReadOnlyList<>`

### UI
- Wszystkie teksty przez `@inject IStringLocalizer<SharedResource> L` + `L["Key"]`
- Klucze dodawane w **3 plikach resx**: `SharedResource.resx` (neutral), `.en.resx`, `.pl.resx`
- MudBlazor 8 switch — `@bind-Value` działa, ale dla nullable użyj `Value` + `ValueChanged` manual handler
- `MudNumericField T="double?"` + null wartość → arrow up/down nie działa → zamień na `MudSlider` z konwersją null↔0

## VLLM — walidacja kontekstowa + biblioteka szablonów (Fazy A-D, 2026-04-22)

**Schema odpowiedzi VLLM** (strict, używany przez wszystkie built-in szablony + generator):
```
observations[] / alternative_explanations[] / severity (none/low/medium/high/critical) /
confidence (0..1) / confirmed (bool) / image_quality (good/acceptable/poor) /
reason (PL) / recommended_action (ignore/monitor/investigate/alert/emergency)
```

**Triple gate w `VllmChecker`** przed fire trigger: `confidence >= MinConfidenceToConfirm` AND `severity >= MinSeverity` AND `(image_quality != poor OR !RequireGoodImageQuality)`. Wszystkie trzy muszą przejść.

**Best-practice w system promptach** (`BuiltInTemplates.CommonSystemRules`):
1. Chain-of-thought: najpierw observe, potem alternatives, dopiero decide.
2. Anti-FP: explicit "alternative_explanations" przed confirmed.
3. Severity rubric z jawnymi definicjami (none/low/medium/high/critical).
4. Confidence calibration: >=0.9 unambiguous, 0.7-0.9 likely, <0.7 do NOT confirm.
5. Image quality self-assessment — poor => confirmed=false.
6. Output: strict JSON, prose (reason/observations) po POLSKU, system prompt po EN (stabilniej).

**Builtin templates** (seed przez `PromptTemplateSeeder`): 12 scenariuszy BHP (PPE-kask, PPE-kamizelka, Pożar, Intruz, Upadek, Wózek-pieszy, Osłona maszyny, Skupienie, Rozlanie, Wysokość, Palenie, Loitering). Identyfikowane przez stabilny `BuiltInKey` — re-seeding safe (nie nadpisuje user edits). Read-only w UI, user robi Duplikuj dla własnej wersji.

**Auto-versioning**: `MongoPromptTemplateRepository.UpdateAsync` override — przed zapisem snapshot poprzedniego stanu do `prompt_template_versions`. UI przegląd historii + rollback w `VllmTemplateStatsDialog`.

**Quality loop (Faza D)**: `Incident.VllmTemplateId + VllmConfidence + WasFalsePositive` agregowane w `GetStatsByVllmTemplateAsync` → FP rate + histogram confidence + avg → widoczne na stronie `/admin/vllm-templates` (kolumna 30d + drill-down dialog z MudChart).

**Multi-provider LLM** — **single source of truth: `LlmProvider` w Mongo**, zarządzane wyłącznie przez `/admin/llm-providers`. Presety per typ (Ollama/LMStudio/vLLM/OpenAI/Azure/Groq/Together/Mistral/DeepSeek/LocalAI). `IChatClientFactory.GetForAsync(providerId)` — cache HttpClient per providerId; gdy `providerId` null/empty → resolwuje providera z `IsDefault=true`; gdy żaden nie istnieje → rzuca `InvalidOperationException`. `VllmCheckConfig.LlmProviderId?` pozwala triggerowi wybrać swój provider. **Zlikwidowane (2026-04-26)**: appsettings sekcja `Llm`, strona `/admin/llm` (`LlmAdmin.razor`), `LlmProviderSeeder` (one-shot migrator z appsettings → DB), bezpośrednia rejestracja `IChatClient` w DI, `LlmOptions.SectionName`. Wszyscy konsumenci LLM-u idą przez `IChatClientFactory` (`Assistant.razor`, `CameraDetail.razor`, `IncidentAnalyzer`, `LlmPromptGenerator`, `VllmChecker`). `LlmOptions` pozostaje jako wewnętrzny DTO budowany przez factory z `LlmProvider` (nie jest już bindowany z appsettings).

**Playground `/admin/vllm-playground`** — upload obrazu, test promptu, Generator z opisu, Refine z feedbackiem, Zapisz jako szablon.

## Open-vocabulary detection (7 faz, 2026-04-23)

**Motywacja**: closed-set YOLO wymaga retreningu per nowa klasa. Faza open-vocab daje "type and compile" UX — operator opisuje klasę tekstem albo pokazuje przykładami, zero ML-dev-loop.

**Architektura** — trzy filary:

1. **`DetectionClass` jako pierwszoklasowa encja** (Domain/Detection) odklejona od `MLModel.Labels`. Kind: `ClosedSetBinding` (legacy binding) / `Text` (prompt) / `Visual` (refs) / `TextAndVisual`. Built-in 14 klas BHP (PPE / Pożar / Ruch / Bezpieczeństwo) seedowane przez `DetectionClassSeeder`, read-only (`BuiltInKey` re-seed-safe). `TriggerCondition.DetectionClassId?` jako alt do legacy `ModelId+Labels` — backward-compat zero breakage.

2. **`ModelCapabilities` flags** na `MLModel` — `ClosedSet / TextPrompts / VisualPrompts`. Rozwarstwione interfejsy detektorów: `IObjectDetector` (base) / `ITextPromptDetector` / `IVisualPromptDetector`. `IDetectorFactory.GetTextPromptDetector(model)` / `GetVisualPromptDetector(model)` z capability-gate + plug-in resolve po `Backend` string.

3. **Dwa detektory, dwie licencje**:
   - **YOLO-World v2-s** (Apache 2.0, Tencent) — **primary open-vocab**, `OnnxYoloWorldDetector`. Auto-detect dynamic (2 inputs) vs compiled. Batch SAHI-friendly.
   - **YOLOE** (AGPL, Ultralytics) — **swap-ready visual-prompt**, `OnnxYoloEDetector`. 2 sessions: detection head + image encoder ViT-224. Cache visual embeddings per-class. **Architektura dokumentowana pod swap na OWLv2 (Apache 2.0) w przyszłości** — 4-krokowy recipe w docstring `DetectorBackend.YoloE`.

**CLIP text encoder — hot-swap między 2 impl** (`IClipTextEncoder`):
- `OnnxClipTextEncoder` (default, offline-first) — ViT-B/32 bundled w `runtime/models/yolo-world-v2-s/text-encoder.onnx`.
- `ExternalLlmClipTextEncoder` — via `IEmbeddingsClientFactory` + OpenAI-compat provider (wymaga CLIP-kompatybilny model; standardowe OpenAI embeddings mają inną przestrzeń latent).
- Wybór przez `YoloWorldOptions.EncoderStrategy` w appsettings.
- `ClipTokenizer` — self-contained CLIP BPE, **celowo bez** `Microsoft.ML.Tokenizers` żeby uniknąć breaking changes w paczkach.

**Compiled prompt packs (persistent cache, nie re-param ONNX)**:
- `CompiledPromptPack` — snapshot `(SourceModelId, EncoderBackend, ClassIds, Prompts, EmbeddingsBlob)`. User klika "Kompiluj" w `/admin/prompt-packs`, `PromptPackCompiler.CompileAsync` liczy embeddings raz i zapisuje blob.
- Przy inferencji `OnnxYoloWorldDetector.ResolveEmbeddingsAsync` próbuje `ICompiledPromptPackRepository.FindByPromptsAsync` — cache hit = skip CLIP encode (~10-30ms/klatka). Graceful fallback.
- **Świadomie nie robimy prawdziwego re-param ONNX grafu** (wymagałby Python sidecar albo OnnxSharp zależność). Persistent embeddings cache daje identyczny gain performance bez tego narzutu. Stale detection via snapshot promptów — user klika "Refresh" gdy zmieni prompt klasy.

**Visual references storage**:
- `FileKind.DetectionClassRef` → `storage/detection-classes/{classId}/refs/{guid}.{ext}`.
- 3 endpointy `/api/detection-classes/{classId}/refs/*` — GET serwuje thumbnail, POST upload (multipart max 5MB jpg/png, Guid filename server-side żeby uniknąć user-controlled paths), DELETE usuwa plik + `VisualReference` z klasy.
- Path-traversal safe: regex walidacja `classId` + `refName`, file dostęp tylko przez `IFileStore`.

**Quality loop per-klasa** (analog Fazy D VLLM templates):
- `Incident.DetectionClassId?` + `DetectionConfidence?` (avg). Index `DetectionClassId + OccurredAt desc`.
- `IIncidentRepository.GetStatsByDetectionClassAsync` → `DetectionClassStats(FireCount / FpCount / AvgConfidence / ConfidenceHistogram[10])`.
- `/admin/detection-classes` kolumna 30d z 3 chipami + `DetectionClassStatsDialog` z MudChart histogram + selektor okna 7/30/90 dni.

**Download script strategy**:
- `yolo-world-v2-s` — Apache 2.0, pobiera z HF automatycznie (default target).
- `yoloe-11s` — AGPL, **interaktywny warning** przed pobraniem (user musi wpisać "y"). README bundled z licence note.

## Homografia i filtry przestrzenne

**Kalibracja** (`/cameras/{id}/calibration`): dwa tryby:
- **Prostokąt** (preferowany) — 4 kliki na rogi prostokąta o znanych wymiarach (np. płytka, miejsce parkingowe), W×D w metrach. Auto-przypisanie world coords (0,0)/(W,0)/(W,D)/(0,D).
- **Punkty** (zaawansowane) — dowolne XY ręcznie, min 4 nie-koliniarne.

**Math**: `HomographyCalculator.Compute(points)` — DLT (8 niewiadomych, h22=1 normalization) + least-squares przez normalne równania (AᵀA·h = Aᵀb) + Gauss z partial pivoting. Zwraca null gdy osobliwe (koliniarne, duplikaty). 5 testów jednostkowych.

**Projekcja** w pipeline: stopa bboxa (bottom-center, `y = bbox.Y + bbox.Height`) → `H.Project(px, py)` → `(Xm, Ym)` na podłodze.

**Spatial filter PairDistance** — embedded w `Trigger.SpatialFilters`. Działa na detekcjach całej klatki (nie tylko strefa). AnyPair = wystarczy jedna para w zakresie, AllPairs = wszystkie. Gate po trigger fire + przed VLLM. Bez homografii → filtr blokuje trigger (bezpieczniej).

## Monitor wall (`/monitor`) — multi-tile

**Architektura** ([monitor.js](src/SafeView.Web/wwwroot/monitor.js)):
- `Map<tileId, {token, timer, abort, ctx}>` — każdy kafel niezależny poller
- Session token per kafel — unieważnia stare ticks przy restart (zmiana interval / sync / show-all)
- `AbortController` przerywa in-flight fetch
- Globalne toggles (shared): ROI / Zones / Detections / Distances / Sync / AllClasses / Interval / MaxPairs

**Klucz: Sync Frame**: `_syncFrame=true` (default) → image z `/cameras/{id}/pipeline-frame.jpg` (tej samej klatki co detekcje — bboxy zgadzają się z obiektami, obraz lekko opóźniony). `false` → live snapshot z kamery (bboxy latają za obiektami).

**Filtr klas**: `/api/monitor/{id}` domyślnie filtruje detekcje do `(ModelId, Labels)` z triggerów przypiętych do stref kamery. `?all=true` wyłącza — user widzi wszystko co model wykrywa (debug).

**Linie dystansu**: tylko top-N najbliższych par (slider, default 3) — inaczej przy 5+ detekcjach overlay nieczytelny.

## Flow topology (`/flow`) — SignalR live events

**Hub** (`/hubs/flow`, policy cameras:view): `TriggerFired / ActionExecuted / VllmRejected / SnapshotCaptured`. Publisher `SignalRFlowEventPublisher` fire-and-forget (nigdy nie rzuca do callera).

**Integracja pipeline**:
- `DetectionPipeline` wstrzykuje `IFlowEventPublisher?` — opcjonalny, emit po trigger fire + VLLM reject.
- `ActionDispatcher` wstrzykuje to samo — emit na końcu `ExecuteOneAsync` + `TestFireAsync` po każdym audit log.

**Node click** → deep-link `?edit={id}&returnTo=/flow` — strona CRUD (Rois/Zones/Triggers/Actions/Models) otwiera dialog automatycznie, `returnTo` w URL odsyła po Save/Cancel. Wymaga `[SupplyParameterFromQuery(Name="edit")]` + `[SupplyParameterFromQuery(Name="returnTo")]` na stronie (patrz Rois.razor jako wzór).

## Kluczowe decyzje architektoniczne

### Detection pipeline (pełna spec w `DETECTION_PIPELINE.md`)

```
Camera → ROI (1..N, prostokąt) → Zone (wielokąt w ROI, 1..N)
                              ↓
       ModelIds (cascade opcjonalnie: Proposer → Confirmers)
                              ↓
       TriggerCondition (AND, per-Trigger) → Trigger (współdzielony)
                              ↓
       [VLLM check opcjonalny] → Action (Log/InApp/Email/Webhook)
                              ↓
       ActionExecution audit log
```

### Tryby inferencji per ROI
- `Native` — crop ≤ model.inputSize, bez resize
- `Resize` — crop > inputSize, prosty resize (traci małe obiekty)
- `Sliced` — SAHI tiling z batch inference (zachowuje małe obiekty w 4K)
- `Adaptive` — auto-wybór przez `TileGrid.PickAdaptiveMode()`

### Cascade mode (Faza 5)
- `Roi.ProposerModelId` — lekki model (np. yolov8n) szuka kandydatów
- Confirmerzy z `ModelIds` działają tylko na bbox-crops z paddingiem — **oszczędza 10× gdy zdarzenia są rzadkie**

### Batch ONNX (Faza 4)
- Modele ONNX eksportowane z `dynamic=True` (zmienione w `download-models.sh`)
- `IBatchObjectDetector.DetectBatchAsync` — N tiles w 1× `session.Run()`
- `SlicedDetector` wykrywa batch capability i używa gdy dostępne (fallback sekwencyjny gdy nie)

### VLLM gating (Faza 3)
- `Trigger.VllmCheck` (opcjonalny) — po trigger fire pyta LLM czy to real alert
- Multimodal: `ChatMessage.ImagePath` → base64 w OpenAI vision format
- Structured output przez JSON Schema (vLLM: `guided_json`, OpenAI: `response_format`)
- `RejectOnError` — fail-closed/fail-open choice

## Komendy które często używasz

```bash
# Build (z repo root)
dotnet build

# Test (wszystkie projekty)
dotnet test

# Uruchomienie aplikacji (jako root — ma dostęp do <1024 portów)
sudo ./start.sh
sudo ./stop.sh
sudo ./restart.sh

# Pobranie binariek (jednorazowo po klonie)
./scripts/download-binaries.sh      # ffmpeg + mediamtx dla 3 platform
./scripts/download-models.sh        # YOLO COCO models (n + s) z dynamic batch

# Logi runtime
tail -f src/SafeView.Web/logs/safeview-$(date +%Y%m%d).log
```

## Częste pułapki (co już raz złapaliśmy)

1. **MediaMTX restart co 30s** — timestamp w YAML config powodował zmianę hash-a. **Fix**: bez dynamic timestamps w configu.
2. **Aplikacja zawisa na starcie** — Serilog MongoDB sink resolve'uje ILogger w ctor → circular. **Fix**: lazy resolve via `IServiceProvider`.
3. **`-stimeout` w ffmpeg 7.x** — usunięte. Dla RTSP: `-timeout`, dla HTTP: `-rw_timeout`.
4. **Apphost locked przez root** — kiedy build jako user, stary proces jako root trzyma plik. **Fix**: `sudo chown -R user bin/ obj/` albo sudo restart.
5. **Blazor `OnAfterRenderAsync` + auto-capture** — parameter może być pusty w firstRender. Użyj guard flag + check na każdym renderze.
6. **Switch OFF w light mode w MudBlazor 8** — selektor CSS `.mud-checked` zmieniony. **Fix**: `:has(input:checked)` selector.
7. **Storage path read-only** — `/Karta1T` może być unmounted. `LocalDiskFileStore` musi łapać `IOException` przy `EnsureDirectoriesExist`.
8. **Legacy Zone.Rules** — refaktor Fazy 1 wymagał drop starych dokumentów. `LegacyZoneCleanupService` to robi przy starcie.
9. **Razor switch expression** — Razor source generator w MudTable context miewa problem z `rate switch { < 0.10 => ..., _ => ... }` (generuje invalid C#). **Fix**: zamień na zwykłe `if/else`. Dotknęło `FpStyle` w `VllmTemplates.razor`.
10. **MudNumericField T="double?"` + null arrow** — strzałki up/down nie działają gdy nullable jest null. Użyj switch do przełączania null↔value + osobny slider gdy ma wartość (patrz `TriggerDialog.MinConfidence`).
11. **MudSlider snap-step feedback loop** — `Step="0.05"` + `ValueChanged` mapujący 0→null → re-render wymusza 0.0 → slider skacze między 0 a 0.05. Fix: nie konwertuj 0→null w drag handlerze, tylko w osobnym switch.
12. **SignalR Hub nie mapuje bez `AddSignalR()`** — mimo że Blazor Server używa SignalR wewnętrznie, custom Hub wymaga `services.AddSignalR()` jawnie w Program.cs.
13. **DialogParameters a CompatibleModelIds** — gdy nowa strona przekazuje dodatkowe parametry do dialogu (np. `Templates`, `LlmProviders`), **wszystkie wołające** strony (Triggers.razor) muszą być zaktualizowane żeby je dostarczyć — parametr bez wartości dialog traktuje jako null.
14. **Hardcoded style w chipach** — nie używaj `Style="background:#3A1515; color:#..."` bo w trybie jasnym wygląda tragicznie. Używaj `MudChip.Color` (Info/Warning/Error/Success/Default) + `Variant`. Gdy light-mode generic overrides zerują kolor — dopisz per-color regułę w `theme-light.css` sekcja CHIPS.
15. **CameraFrameSampler nie ma globalnego throttla** — sampler budzi się adaptacyjnie per-camera (zgodnie z `SnapshotIntervalSeconds`). Jeśli jedna kamera ma 1s a druga 60s — obie działają w swoich kadencjach. Dolny limit to `MinTickMilliseconds` (default 250ms).
16. **DetectionSnapshotStore jest in-memory singleton** — reset po restarcie. Multi-instance = niezsynchronizowane. Flow/Monitor zakłada single-instance deployment.
17. **OnnxAbsolutePath vs OnnxRelativePath** — bundled modele (z `runtime/models/`) używają Absolute; uploaded używają Relative. Walidacja ModelDialog musi akceptować któryś z nich (nie tylko Relative — to był bug).
18. **Per-provider HttpClient cleanup** — `ChatClientFactory.Invalidate(id)` usuwa cached HttpClient z mapy i disposuje go. Zawsze wołaj po edycji providera w UI (inaczej stary klient z starym kluczem zostaje). Analogicznie `EmbeddingsClientFactory.Invalidate`.
19. **CA1859 na perf hinty** — analyzer wymusza `List<T>` zamiast `IReadOnlyList<T>` gdy property/parameter jest kolekcja; `ConcreteType` zamiast interfejsu w return type gdy metoda nie potrzebuje interface. Testy mają wyłączone (`<NoWarn>$(NoWarn);CA1707;CA1861;CA1305;CA1310</NoWarn>` w csproj), production code musi się zastosować.
20. **Razor generator a switch expression z range patterns** (rozszerzenie pkt 9) — `bytes switch { < 1024 => "B", < 1048576 => "KB", _ => "MB" }` generuje invalid C# nawet w `@code` bloku. Fix: if/else. Dotknęło `FormatBytes` w `PromptPacks.razor`.
21. **DetectionClass backward-compat z TriggerCondition** — `TriggerCondition.DetectionClassId?` jest opcjonalne; gdy null, matcher używa legacy `ModelId+Labels`. `TriggerConditionMatcher.Matches(cond, det, classes, enforceMinConf)` rozstrzyga per-condition. Przy toggle w UI (TriggerDialog switch "użyj klasy z biblioteki") zawsze czyść pola drugiej ścieżki żeby uniknąć ambiguity.
22. **CLIP embeddings nie są provider-agnostic** — ExternalLlmClipTextEncoder wymaga CLIP-kompatybilnego provider embeddings. Standardowe OpenAI `text-embedding-3-small` ma INNĄ przestrzeń latent i nie zadziała z YOLO-World. User musi mieć self-hosted CLIP-as-a-service albo LocalAI z załadowanym CLIP. Dokumentacja w `IEmbeddingsClient` + `ExternalLlmClipTextEncoder` to jawnie pokazuje.
23. **YOLOE AGPL swap-ready** — implementacja `OnnxYoloEDetector` jest "pierwsza", nie "ostatnia". Nigdy nie pisz kodu poza tą klasą + `DetectorBackend` enum + DI który zna "YOLOE" specyficznie. Matching capability-driven przez `ModelCapabilities.VisualPrompts`. Swap na OWLv2 (Apache 2.0) to 4 kroki: enum + impl + DI + switch case. Zero zmian w Domain / Trigger / UI.
24. **Visual refs embedding cache nie w Domain** — `VisualReference` trzyma tylko crop path + metadata. Vector cache (per-detector: YOLOE CLIP-image ≠ hypothetical OWLv2 SigLIP) żyje w detektorze (`ConcurrentDictionary<classId, float[]>`). Swap backendu = cache invalidation, user data zostaje nietknięta.
25. **Prompt pack staleness** — `CompiledPromptPack.PromptSnapshots` porównywany z aktualnymi `DetectionClass.TextPrompt` via `PromptPackCompiler.RefreshStalenessAsync`. Obecnie user ręcznie klika "Refresh" (UI button); auto-invalidation przy UpdateAsync klasy jest follow-up ticket #39.
30. **YOLO-World ONNX export pułapka** (2026-04-27) — `ultralytics yolo export model=yolov8s-worldv2.pt format=onnx` produkuje **closed-set** model z zamrożonym vocab COCO (1 input, brak text path). Custom prompts są ignorowane przez sieć. Naprawione: pobieramy z `jquadrino/yolo-world-onnx` (true dynamic 2-input, output split scores+boxes). `OnnxYoloWorldDetector` ma defensive guard rzucający clear error gdy `isDynamic=false` + custom prompts. `Postprocess` autodetect-uje 1-output (fused YOLOv8) vs 2-output (split scores+boxes xyxy). Endpoint `/api/models/{id}/test-detect` zwraca `warning` field gdy fallback do default flow ignoruje user prompts.
31. **CLIP text encoder pre-vs-post projection** (2026-04-27) — Xenova/clip-vit-base-patch32/onnx/text_model.onnx zwraca POST-projection embeddings (`text_projection @ pooler_output`), ale YOLO-World oczekuje PRE-projection (sam `pooler_output`) bo ma własny text_projection layer fused w wagach. Karmienie post-projection = double projection = garbage scores: na zdjęciu z ludźmi tylko "person" (super-rozpoznawalny embedding) wykrywało, "pants"/"tshirt"/"shirt" → 0.0009 max score → 0 detekcji. **Naprawione**: `scripts/export-clip-text-encoder.py` eksportuje samodzielnie z `transformers` (PyTorch) zwracając `pooler_output`. `download-models.sh` woła ten skrypt zamiast pobierać z HF (pre-built ONNX zwracające pooler_output po prostu nie istnieje na HF — wszystkie znane CLIP exports dają text_features). Pamiętaj: gdy widzisz cos similarity ~0.04 między swoim ONNX a referencyjnym CLIP text — to znak że masz odwrotną wersję projection.
32. **CLIP padding token = EOS, NIE 0** (2026-04-27) — `vocab[0]` w CLIP-ie to znak `"!"`. Padding tokenem 0 contaminuje embedding krótkich promptów przez transformer attention layers — "face" (1 token + 74×"!" w padding-u) miało zaszumiony embedding nie do rozróżnienia od noise. HuggingFace `CLIPTokenizer` pad-uje EOS (49407) — nasz `ClipTokenizer.PadToken` musi być `EosToken`. Bug naprawiony 2026-04-27.
33. **YOLO-World v2 USUNIĘTY 2026-04-27, OWLv2 jako jedyny open-vocab detektor** — YW dawał false positives: prompty matchowały wizualnie podobne fragmenty (np. czerwone paski na ustach Benetton modeli jako "pants"), nie prawdziwe obiekty. Pomimo trzech naprawionych bugów (closed-set ultralytics export, CLIP pad token, post-vs-pre projection embeddings), klasa modelu zostawała słaba dla rzadkich klas. Usunięte: `OnnxYoloWorldDetector`, `DetectorBackend.YoloWorld` enum value (gap przy 2), `DownloadYoloWorldEndpoint`, cały feature `CompiledPromptPack` (był YW-specific cache embeddings), `runtime/models/yolo-world-v2-s/model.onnx` (text-encoder.onnx + tokenizer/ zachowane bo używa ich YOLOE), `MLModel.SourceModelId/CompiledClassIds`, page `/admin/prompt-packs`, 53 resx keys × 3 lokale. Zastąpione przez **OWLv2 base/large** (Google, Apache 2.0, ViT-based, single fused ONNX 960×960, single `OnnxOwlV2Detector` w `src/SafeView.ML/OwlV2/`). 2 modele dostępne: `owlv2-base` (153M, 614MB, fast) i `owlv2-large` (430M, 1.74GB, best quality, 3-4× slower). User przełącza w `/models` test detect dropdown — pipeline agnostic (`ITextPromptDetector` factory routing). Bundled: `runtime/models/owlv2-base/` i `runtime/models/owlv2-large/`.
26. **IJSRuntime fetch zamiast HttpClient** w Blazor Server dialogs — nie rejestrujemy `HttpClient` w DI dla Web. Upload/delete przez `IJSRuntime.InvokeAsync("eval", ...)` z FormData — request idzie z przeglądarki user-a, auth cookies + CSRF auto-forwarded. Pattern widoczny w `DetectionClassDialog.UploadRef` i `VllmTemplates.ImportFile`.
27. **ModelSeeder auto-detect backend po strukturze folderu** — `runtime/models/{name}/` z obecnością `text-encoder.onnx + image-encoder.onnx + tokenizer/` → YoloE (wszystkie 3 caps). Tylko `text-encoder.onnx + tokenizer/` → YoloWorld. Inaczej → classical Onnx. Gdy dodajesz nowy backend (np. OWLv2), rozpoznawanie dodaj w jednym miejscu (`ModelSeeder.StartAsync`).

## Permisje (lista kompletna w `Permission.All`)

```
admin:users, admin:roles, admin:settings, admin:license, admin:audit,
admin:system-events, admin:api_keys, admin:notifications,
admin:rois, admin:triggers, admin:actions, admin:detection-classes,  # Faza 1+
cameras:view, cameras:edit, cameras:delete,
zones:view, zones:edit,
models:view, models:edit, models:upload,
incidents:view, incidents:resolve,
reports:view, reports:generate,
llm:chat, llm:configure,
api:incidents:read, api:cameras:read, api:zones:read  # API v1 scopes
```

`IdentitySeeder` automatycznie dodaje wszystkie nowe permisje do roli Admin przy starcie (synchronizacja built-in roles).

## Pliki referencyjne (zawsze czytaj przed pracą w danym obszarze)

- Pipeline detekcji: `src/SafeView.Application/Detection/DetectionPipeline.cs`
- Trigger logic: `src/SafeView.Application/Detection/TriggerEvaluator.cs`
- Trigger matcher (legacy + class-based): `src/SafeView.Application/Detection/TriggerConditionMatcher.cs`
- SAHI tiling: `src/SafeView.Domain/Detection/Geometry/TileGrid.cs`
- ONNX inference classical: `src/SafeView.ML/OnnxObjectDetector.cs`
- ONNX YOLO-World (Apache 2.0, primary open-vocab): `src/SafeView.ML/YoloWorld/OnnxYoloWorldDetector.cs`
- CLIP text encoder (in-process + external): `src/SafeView.ML/YoloWorld/OnnxClipTextEncoder.cs` + `ExternalLlmClipTextEncoder.cs`
- CLIP BPE tokenizer (self-contained): `src/SafeView.ML/YoloWorld/ClipTokenizer.cs`
- ONNX YOLOE (AGPL, swap-ready): `src/SafeView.ML/YoloE/OnnxYoloEDetector.cs`
- DetectorFactory plug-in resolve: `src/SafeView.ML/DetectorFactory.cs`
- DetectionClass library + built-in: `src/SafeView.Domain/Detection/DetectionClass.cs` + `src/SafeView.Infrastructure/Detection/BuiltInDetectionClasses.cs`
- Compiled prompt packs: `src/SafeView.Application/Detection/PromptPackCompiler.cs` + `src/SafeView.Domain/Detection/CompiledPromptPack.cs`
- Embeddings client (OpenAI-compat): `src/SafeView.LLM/OpenAiCompatibleEmbeddingsClient.cs` + `EmbeddingsClientFactory.cs`
- Visual refs endpoints: `src/SafeView.Web/Endpoints/DetectionClassRefEndpoints.cs`
- Action dispatching: `src/SafeView.Application/Detection/ActionDispatcher.cs`
- VLLM checker: `src/SafeView.LLM/VllmChecker.cs` (triple gate + strict schema)
- VLLM multimodal: `src/SafeView.LLM/OpenAiCompatibleChatClient.cs`
- VLLM templates seed: `src/SafeView.Infrastructure/Vllm/BuiltInTemplates.cs` (CommonSchema + CommonSystemRules)
- VLLM generator: `src/SafeView.LLM/LlmPromptGenerator.cs` (MetaPrompt + RefineAsync)
- Multi-provider LLM: `src/SafeView.LLM/ChatClientFactory.cs` + `src/SafeView.Domain/Llm/LlmProvider.cs`
- Homografia: `src/SafeView.Domain/Cameras/HomographyCalculator.cs` (DLT)
- Spatial filters: `src/SafeView.Application/Detection/SpatialFilterEvaluator.cs`
- Monitor multi-tile: `src/SafeView.Web/wwwroot/monitor.js` (session-token per-tile)
- Flow topology: `src/SafeView.Web/wwwroot/flow.js` + `src/SafeView.Web/Hubs/FlowHub.cs`
- In-app notifications: `src/SafeView.Application/Notifications/InAppNotificationBroker.cs`
- Security policies: `src/SafeView.Security/Auth/Policies.cs`
- DI root: `src/SafeView.Web/Program.cs`

---

## ApiCamera (push-based ingest, 2026-04-26)

**Motywacja**: nie tylko RTSP/HTTP/File-loop — niektóre wdrożenia mają zewnętrzny inference (edge appliance, Frigate, własny serwer ML) który robi detekcje lokalnie i chce pchać wyniki do SafeView. Dodaliśmy `CameraTransport.Api` + REST ingest endpoint.

**Architektura — granica systemu vs reszta**:
- **Granica (specjalna)**: `CameraVendor.ApiPush + CameraTransport.Api` w domain. `IngestEndpoints` (`POST /api/v1/cameras/{id}/ingest` multipart + `/ingest-json` z base64/URL fallback). `ApiCameraProvisioner` auto-tworzy pełnokadrową ROI (`IsFullFrame=true, Rectangle=(0,0,1,1)`) + Zone (4-punkt polygon `(0,0)-(1,0)-(1,1)-(0,1)`) przy save kamery.
- **Pipeline (rozdzielenie)**: `IDetectionPipeline.ProcessExternalDetectionsAsync(camera, framePath, frameRelPath, externalDetections, capturedAt, ct)` — pomija stage inferencji, wpada do wspólnej `EvaluateAndDispatchAsync(...)` używanej też przez `ProcessFrameAsync`. Wspólna metoda robi: snapshot store update, batch load triggers/actions/classes, homography compute, eval + VLLM gate + actions + audit + flow events. **Downstream nie wie skąd są detekcje.**
- **Reszta (bez zmian)**: `TriggerEvaluator`, `TriggerConditionMatcher`, `VllmChecker`, `ActionDispatcher`, `IncidentRepository`, `DetectionSnapshotStore`, `FlowHub`. Incident ma `OccurredAt = frame.captured_at` (czas sendera, nie server clock).

**Format API**: schema `1.0` zbliżona do Roboflow Inference. Bbox w **pixel coords** (top-left origin) — server normalizuje do `[0..1]`. `ModelId="external"` constant — używaj DetectionClass-based conditions (nie ModelId+Labels). Idempotency po `frame_id` (in-memory LRU 1000 ostatnich per kamera). Pola opcjonalne: `track_id`, `polygon`, `keypoints`, `attributes` (forward-compat). Per-camera pin via `Camera.IngestApiKeyId` jako defense-in-depth ponad scope `api:cameras:write`.

**Bezpieczeństwo**: rate-limit policy `camera-ingest` partycjonowany per `cameraId` (1800 req/min). Walidacja bbox-bounds, confidence, image size (max 20MB), JSON metadata (max 1MB). JSON-only `image_url` ograniczony do http(s) + 5s timeout + 10MB defense vs SSRF/DOS. Endpoint wymaga `api:cameras:write` scope; transport-check rzuca `BadRequest` gdy ktoś próbuje pisać do kamery RTSP.

**UI**: `CameraVendor.ApiPush` w dropdownie Vendor → wymusza Transport.Api. Panel pokazuje URL endpointu + 2 expansion panels z curl examples (multipart + JSON-only) + pole `IngestApiKeyId` do pin-owania klucza. Hide Connection/Sampling sections (sampler nie polluje). Sampler ma guard `c.Transport != Api` żeby skip-nąć.

**Pełna dokumentacja**: `documentation/API_INGEST.md` (curl examples, response codes, format spec, smoke test E2E).

## Swagger / OpenAPI (`/swagger`, 2026-04-26)

**Stack**: `Swashbuckle.AspNetCore` 7.2.0 — bundluje SwaggerUI assets w NuGet packagecie (offline-first, brak CDN runtime). Spec generowany dla endpointów `/api/v1/*` (REST API + ingest), reszta (Blazor SignalR, `/auth/*`, `/culture/set`, snapshot endpointy) odfiltrowana przez `DocInclusionPredicate`.

**Endpointy**:
- `GET /swagger/v1/swagger.json` — OpenAPI 3.0 spec
- `GET /swagger` — interactive UI (try endpoints z Authorize button dla API key)

**Auth**: cookie-gate przez custom middleware (anon → redirect `/login?ReturnUrl=/swagger`). Spec definiuje security scheme `ApiKey` (type=apiKey, in=header, name=Authorization, format=`Bearer YOUR_KEY`) — jest globalny security requirement, czyli każdy operation w UI ma "Authorize" button.

**Endpoint metadata**: `.WithTags("Group")` + `.WithSummary("...")` + `.WithDescription("...")` + `.Produces<T>(200)` + `.ProducesProblem(400/401/etc)` na każdym endpoincie w `ApiV1Endpoints` i `IngestEndpoints`. To powoduje że UI grupuje endpointy po tag-ach (Incidents/Cameras/Zones/Health/Ingest) z opisem, schematami request/response i statusami.

**Kiedy dodajesz nowy endpoint**: po `.RequireAuthorization(...)` dorzuć `.WithTags("Group").WithSummary("Krótko").Produces<T>(200).ProducesProblem(401)`. Bez tego endpoint pojawi się w UI bez opisu (brzydko, ale działa).

**Vendor note**: Swashbuckle bundluje SwaggerUI w NuGet (embedded resources). Nie wymaga vendoringu do `wwwroot/vendor/`. To wyjątek od reguły offline-first (asset jest częścią DLL-a, nie HTTP fetch).

---

**Gdy user pyta "co dalej" bez kontekstu** — przeczytaj `documentation/ROADMAP.md` i zaproponuj 3 najważniejsze tickety.
