# Detection Pipeline — Architektura systemu wykrywania incydentów

**Status**: draft (2026-04-20), Faza 1 do implementacji
**Źródło**: `documentation/flow_detection.pdf` + wymagania użytkownika z sesji planowania

---

## 1. Cel i kontekst

Obecny pipeline SafeView (`DetectionFrameObserver`) ma ograniczenia:

- Modele ML są **globalne** — każdy enabled model uruchamia się na każdej klatce każdej kamery. Brak granularności.
- Reguły detekcji są zagnieżdżone w `Zone.Rules` — niemożliwe do re-używania między strefami.
- Akcje (incident + notifications) są **zahardkodowane** — nie można ich rozszerzyć o Relay, SMS, Webhook bez zmian w kodzie.
- Nie ma warstwy kontekstowej walidacji (np. VLLM sprawdzający czy to rzeczywiście ogień, a nie odblask).
- Kamery 4K muszą być downsampled do 640×640 przed inferencją — **drobne obiekty gubią się** (osoba na 50m w 4K ma ~30px, po resize ~5px).

Nowa architektura rozwiązuje wszystkie te problemy przez uporządkowany pipeline:

```
Camera → ROI → Zone (w ROI) → TriggerConditions (per-model) → Trigger → [VLLM?] → Action
```

## 2. Domain Model

### 2.1 Diagram encji

```
┌──────────┐       ┌──────┐       ┌──────┐
│  Camera  │──1:N──│ ROI  │──1:N──│ Zone │──N:M──┐
└──────────┘       └──────┘       └──────┘       │
                      │              │           │
                      │ N:M          │           │
                      ▼              │           │
                  ┌─────────┐        │        ┌──────────┐
                  │ MLModel │◄───────┘────────│ Trigger  │──N:M──┐
                  └─────────┘                 └──────────┘       │
                                                   │             │
                                                   │             ▼
                                                   │         ┌────────┐
                                                   │         │ Action │
                                                   │         └────────┘
                                                   ▼             │
                                           ┌───────────────┐     │
                                           │ ActionExec    │◄────┘
                                           │ (audit, TTL)  │
                                           └───────────────┘
```

### 2.2 Encje

#### Camera (istnieje, bez zmian)
- `Id`, `Name`, `SourceUrl`, `Vendor`, `Resolution` (width/height)
- `SnapshotIntervalSeconds` — jak często sampler pobiera klatki

#### ROI (NEW)
*Prostokątny wycinek kadru kamery, w którym działają modele ML.*

| Pole | Typ | Opis |
|------|-----|------|
| `Id` | string | — |
| `CameraId` | string (FK) | — |
| `Name` | string | np. "Hala produkcyjna" |
| `Rectangle` | `{ X, Y, Width, Height }` | współrzędne znormalizowane 0-1 względem kadru kamery |
| `ModelIds` | `List<string>` | modele działające na tym ROI |
| `InferenceMode` | enum: `Native`/`Resize`/`Sliced`/`Adaptive` | tryb inferencji (Faza 2 dla Sliced/Adaptive; Faza 1 używa Native/Resize) |
| `TileSize` | int = 640 | rozmiar tile dla Sliced mode |
| `TileOverlap` | float = 0.2 | nakładka tiles (anty-granica) |
| `Enabled` | bool | — |

**Walidacja**: `Rectangle` musi być w [0,1], `Width`/`Height` > 0.05 (min 5% kadru).

#### Zone (MODIFIED)
*Wielokąt wewnątrz ROI, w którym sprawdzamy triggery.*

| Pole | Typ | Zmiana |
|------|-----|--------|
| `Id` | string | — |
| `CameraId` | string (FK) | zachowane (redundant, dla szybkich query) |
| `RoiId` | string (FK) | **NOWE — required.** Zone musi należeć do ROI |
| `Name` | string | — |
| `Polygon` | `List<ZonePoint>` (0-1 w układzie **kamery**) | zachowane |
| `Color` | string | — |
| `Enabled` | bool | — |
| `TriggerIds` | `List<string>` (FK) | **NOWE**. Zastępuje stare `Rules`. Referencje do współdzielonych Trigger-ów. |
| ~~`Rules`~~ | ~~List<ZoneRule>~~ | **USUNIĘTE**. Logika migruje do Trigger. |

**Walidacja geometryczna (B)**: wszystkie punkty `Polygon` muszą leżeć wewnątrz `Roi.Rectangle`. UI pokazuje walidację na żywo, Save blokowany gdy niepasuje.

#### MLModel (istnieje, bez strukturalnych zmian)
Modele są wciąż globalne, ale uruchamiane **tylko** dla ROI które je referencjonują przez `ROI.ModelIds`.

#### Trigger (NEW, globalny, re-używalny)

| Pole | Typ | Opis |
|------|-----|------|
| `Id`, `Name`, `Description`, `Enabled` | | — |
| `Conditions` | `List<TriggerCondition>` | **AND logic** — wszystkie warunki muszą być spełnione w jednej klatce |
| `CooldownSeconds` | int = 30 | minimalny odstęp między odpaleniami tego Triggera dla tej samej Zone |
| `PersistentFrames` | int = 1 | wymagane N klatek z rzędu ze spełnionymi warunkami (redukuje false positives z flickering) |
| `Schedule` | `TriggerSchedule?` | opcjonalne okno czasowe (dni tygodnia + godziny) |
| `ActionIds` | `List<string>` (FK) | akcje do odpalenia |
| `VllmCheck` | `VLLMCheckConfig?` | **Faza 3** — opcjonalna walidacja kontekstowa |

#### TriggerCondition (embedded)
*Jedna klauzula AND triggera. Odwołuje się do konkretnego modelu.*

| Pole | Typ | Opis |
|------|-----|------|
| `ModelId` | string | który model ma wykryć |
| `Labels` | `List<string>` | jakie klasy: `["person"]`, `["person","no_helmet"]` |
| `BboxRule` | enum: `CenterInZone` / `AnyCorner` / `AllCorners` / `NCorners` / `Iou` | kryterium przynależności bbox do strefy |
| `CornersRequired` | int? | dla `NCorners` (np. 2 z 4) |
| `IouThreshold` | float? | dla `Iou` (np. 0.3) |
| `MinConfidence` | float? | override na model-level default |
| `MinCount` | int = 1 | minimum obiektów tego typu w strefie |

**Przykład**: *„przynajmniej 1 person + przynajmniej 1 forklift, oba z center w strefie"*:
```yaml
Conditions:
  - ModelId: yolov8_person, Labels: [person], BboxRule: CenterInZone, MinCount: 1
  - ModelId: yolov8_forklift, Labels: [forklift], BboxRule: CenterInZone, MinCount: 1
```

#### Action (NEW, polimorficzna)

| Pole | Typ | Opis |
|------|-----|------|
| `Id`, `Name`, `Enabled` | | — |
| `Type` | enum: `LogAlert` / `InAppNotification` (Faza 1) / `Email` / `Webhook` / `Relay` / `Sms` (Faza 4) | — |
| `Config` | `Dictionary<string, string>` | parametry zależne od typu (np. template message) |
| `RateLimitPerMinute` | int = 10 | max wywołań tej akcji na minutę (sliding window) |

**Handler pattern**: dla każdego typu istnieje `IActionHandler` (np. `LogAlertHandler`, `InAppNotificationHandler`). Dodanie nowego typu = dodanie nowej klasy + rejestracja w DI. **Brak zmian w domain.**

#### ActionExecution (NEW, audit log, TTL 30 dni)

| Pole | Typ | Opis |
|------|-----|------|
| `Id`, `CreatedAt` | | — |
| `TriggerId`, `ActionId`, `CameraId`, `ZoneId` | string (FK) | kontekst |
| `FrameSnapshotPath` | string | ścieżka do klatki która odpaliła trigger |
| `Detections` | JSON | zrzut detekcji (debug) |
| `Status` | enum: `Success` / `Failed` / `Skipped` (cooldown/schedule) / `RateLimited` | — |
| `ErrorMessage` | string? | — |
| `DurationMs` | int | — |

**TTL index**: `CreatedAt` → ExpireAfter = 30 dni. Tak samo jak `system_events`.

## 3. Runtime Flow

### 3.1 Algorytm per-klatka

```
1. CameraFrameSampler.TickAsync() wywołuje Camera.Snapshot()
2. Pobierz wszystkie enabled ROI dla tej kamery: List<ROI>
3. Dla każdego ROI:
   a. Crop klatki do ROI.Rectangle (zachowanie oryginalnej rozdzielczości)
   b. Dla każdego ROI.ModelIds:
      i.   Wybór strategii wg ROI.InferenceMode:
             Native  → crop ≤ model.inputSize → bezpośrednia inferencja
             Resize  → crop > model.inputSize → resize do inputSize → inferencja
             Sliced  → [Faza 2] tiling + batch inference
             Adaptive→ [Faza 2] auto-pick
      ii.  Inferencja zwraca detekcje w układzie ROI
      iii. Przetransformuj do układu kamery (offset o Rectangle.X,Y)
4. Zbierz WSZYSTKIE detekcje (ze wszystkich ROI, wszystkich modeli)
5. Dla każdej enabled Zone tej kamery:
   a. Odfiltruj detekcje wpadające do Zone (per-condition bbox rule)
   b. Dla każdego Trigger w Zone.TriggerIds:
      i.   Sprawdź wszystkie TriggerCondition (AND logic):
             - Grupuj detekcje per ModelId
             - Każdy condition musi mieć ≥ MinCount matchujących detekcji
      ii.  Sprawdź PersistentFrames (licznik klatek z rzędu per (trigger, zone))
      iii. Sprawdź Cooldown (ostatnie odpalenie tego trigger dla tej zone)
      iv.  Sprawdź Schedule (DayOfWeek, godzina)
      v.   Jeśli wszystko OK:
             [Faza 3] VLLM check → jeśli confirmed
             ActionDispatcher.FireAsync(Trigger.ActionIds, context)
             └─ dla każdej Action:
                - Sprawdź RateLimit per action
                - Handler.HandleAsync(action, context)
                - Zapisz ActionExecution (sukces/fail)
```

### 3.2 Stan runtime (in-memory)

`TriggerEvaluator` trzyma stan per `(TriggerId, ZoneId)`:
```
{
  lastFiredAt: DateTime,            // dla Cooldown
  consecutiveFrameHits: int,         // dla PersistentFrames
  currentFrameActive: bool,
}
```

Clear tego stanu na restart aplikacji — wszystkie triggery zaczną od zera.

## 4. Optymalizacja wydajności 4K (kluczowa)

### 4.1 Problem

- Kamera 4K: 3840×2160 = 8.3 MP
- YOLO natywnie: 640×640 = 0.4 MP (20× mniej pikseli)
- Osoba 30px w 4K → po resize na 640 ma ~5px → model NIE wykrywa
- Downsampling całej klatki = utrata drobnych/dalekich obiektów

### 4.2 Rozwiązanie

**ROI-based cropping** (Faza 1) + **Sliced Inference** (Faza 2) + **Adaptive** (Faza 2).

| Tryb | Kiedy użyć | Algorytm | Czas inference (1080p ROI) |
|------|-----------|----------|---------------------------|
| `Native` | ROI ≤ model.inputSize (np. 500×500 ROI + 640 model) | Pad do 640, 1× inference | ~30ms |
| `Resize` | ROI > model.inputSize, obiekty duże | Resize do 640, 1× inference (drobne obiekty gubi) | ~30ms |
| `Sliced` | ROI > model.inputSize, obiekty drobne | Tiling 640×640 z overlap 20%, batch inference, NMS merge | ~100-150ms (12 tiles batch) |
| `Adaptive` | auto | Wybiera między Resize a Sliced na podstawie config + statystyk ostatnich klatek | — |

### 4.3 Sliced Inference (SAHI) — algorytm (Faza 2)

```
Input: crop ROI (np. 2000×1500), model.inputSize = 640, overlap = 0.2

1. step = 640 * (1 - 0.2) = 512
2. tiles_x = ceil((2000 - 640) / 512) + 1 = 4
3. tiles_y = ceil((1500 - 640) / 512) + 1 = 3
4. Generuj 12 tiles 640×640 (ostatnie tiles mogą być przesunięte żeby nie wyjść poza ROI)
5. Batch inference — 1 wywołanie ONNX z 12-elementowym batchem
6. Dla każdego tile'a: transform bbox (localX + tileOffsetX, localY + tileOffsetY)
7. Global NMS — usuń duplikaty na granicach tiles (IoU > 0.5)
```

Zalety: **zachowujemy czułość drobnych obiektów** przy kosztach ~3-4× inference time (nie 12×, bo batch + GPU parallel).

### 4.4 Dalsza optymalizacja (Faza 5, opcjonalna)

**Dwuetapowy pipeline** — lekki "proposer" + ciężki "confirmer":
1. Model 1 (mały, np. YOLOv8n): działa na całej klatce resize — znajduje "zainteresowane" regiony (low precision, high recall)
2. Model 2 (większy, np. YOLOv8l): działa tylko na cropach z Modelu 1 — precyzyjna klasyfikacja

Można wdrożyć po Fazie 1 jeśli wydajność będzie problemem.

## 5. Bezpieczeństwo i permisje

**Nowe permisje** (tylko rola Admin, seed przez `IdentitySeeder`):
- `admin:rois` — zarządzanie ROI
- `admin:triggers` — zarządzanie Trigger-ami
- `admin:actions` — zarządzanie Action-ami + przegląd ActionExecution

Obecne permisje zostają:
- `zones:view` / `zones:edit` — edycja stref (użytkownik przypina triggery z biblioteki)

## 6. Fazowanie

### Faza 1 — Core (bez SAHI, bez VLLM) — ~5-7 dni

**Scope:**
- Domain: ROI, Zone (refaktor), Trigger, Action, ActionExecution
- Inference: tylko `Native` + `Resize` (bez Sliced)
- Action types: `LogAlert`, `InAppNotification`
- UI: `/rois`, `/triggers`, `/actions`, `/actions/history`, refaktor `ZoneEditorDialog`
- Permisje: `admin:rois`, `admin:triggers`, `admin:actions`
- Serwisy: `DetectionPipeline` (zastępuje `DetectionFrameObserver`), `TriggerEvaluator`, `ActionDispatcher`
- Migracja: **drop kolekcji `zones`** przy starcie (user re-definiuje strefy)

**Deliverable**: działający pipeline, user może:
1. Zdefiniować ROI na kamerze (prostokąt)
2. Zdefiniować Zone wewnątrz ROI (wielokąt, walidacja że mieści się w ROI)
3. Zdefiniować globalne Triggery (warunki AND z różnych modeli)
4. Zdefiniować globalne Actions (LogAlert / InAppNotification)
5. Przypiąć Triggery do Zone
6. Zobaczyć log odpalonych akcji w `/actions/history`

### Faza 2 — Performance & reliability — ~3-4 dni

- Sliced Inference (SAHI pattern) jako `SlicedDetector` dekorator nad `IDetector`
- Batching ONNX calls
- Tryby `Sliced` i `Adaptive` w ROI
- Monitoring wydajności: inference time per ROI/model do `system_events`
- Dashboard: "co ile ms przetwarzana jest klatka", "hit rate per model"

### Faza 3 — VLLM gating — ~3 dni

- `VLLMCheckConfig` w Trigger
- Reuse `SafeView.LLM`
- Structured JSON response (JSON Schema validation)
- Action odpalany tylko gdy `confirmed: true`
- UI: editor promptu + podgląd response

### Faza 4 — Rozszerzalność Action — ~2-3 dni

- `Email`, `Webhook`, `Relay` (Modbus), `Sms`
- Pluginowy `IActionHandler`
- Rate limiting per-action

### Faza 5 — Opcjonalnie: dwu-etapowy pipeline

- Cascade modeli (proposer + confirmer)

## 7. Migracja i breaking changes

**User zdecydował: czysty restart (bez migracji Zone.Rules → Trigger).**

Implementacja:
- Nowy `IHostedService` przy starcie — jeśli kolekcja `zones` ma dokumenty bez pola `roiId` → loguje Warning i je usuwa (bo są w starym schema)
- User jest zobligowany **re-definiować strefy** po wdrożeniu Fazy 1. Komunikat w UI: *„Stare strefy zostały zresetowane w wyniku aktualizacji pipeline'u detekcji — zdefiniuj je ponownie w ramach ROI."*

**Żaden inny kod nie jest breakującą zmianą** — MLModel, Camera, Incident, Notification zostają.

## 8. UI/UX — mapa stron

| Strona | Route | Permisja | Funkcja |
|--------|-------|----------|---------|
| ROI (lista + edytor) | `/rois` | `admin:rois` | CRUD ROI, podgląd kamery, rysowanie prostokąta |
| Triggery | `/triggers` | `admin:triggers` | CRUD Trigger, editor Conditions (multi-model AND), schedule, cooldown |
| Actions | `/actions` | `admin:actions` | CRUD Action, wybór typu + config |
| Historia Actions | `/actions/history` | `admin:actions` | Read-only lista ActionExecution z filtrami |
| Zones (refaktor) | `/zones` | `zones:view/edit` | Edycja: wybór ROI, multi-select Triggery, walidacja geometrii |
| Nav Menu | — | — | Nowe linki pod sekcją "Administracja" |

Wszystkie pages jawnie `[Authorize(Policy = "perm:...")]` — zgodnie z zasadą z `/Users/robertoswald/.claude/plans/compressed-skipping-dusk.md`.

## 9. Audit trail

Każde wywołanie triggera zapisuje `ActionExecution` — audit trail kto kiedy co wywołał. W połączeniu z:
- `audit_log` (user actions) — z poprzednich faz
- `system_events` (errors/warnings) — z poprzednich faz
- `action_executions` (trigger fires) — **NEW**

Daje pełną widoczność stanu systemu.

## 10. Plan implementacji Fazy 1 — kolejność plików

1. **Domain** (`src/SafeView.Domain/`):
   - `Detection/Roi.cs` + `RoiRectangle.cs` (value object)
   - `Detection/Trigger.cs` + `TriggerCondition.cs` + `TriggerSchedule.cs`
   - `Detection/DetectionAction.cs` + `ActionType.cs` (enum, nazwa `DetectionAction` żeby nie kolidować z `System.Action`)
   - `Detection/ActionExecution.cs`
   - `Zones/Zone.cs` — dodanie `RoiId`, `TriggerIds`, usunięcie `Rules`
   - `Users/Permission.cs` — dodanie `AdminRois`, `AdminTriggers`, `AdminActions`

2. **Application** (`src/SafeView.Application/`):
   - `Abstractions/Persistence/IRoiRepository.cs`, `ITriggerRepository.cs`, `IActionRepository.cs`, `IActionExecutionRepository.cs`
   - `Abstractions/Detection/IDetectionPipeline.cs`, `ITriggerEvaluator.cs`, `IActionDispatcher.cs`, `IActionHandler.cs`

3. **Infrastructure** (`src/SafeView.Infrastructure/`):
   - `Persistence/MongoRoiRepository.cs`, `MongoTriggerRepository.cs`, `MongoActionRepository.cs`, `MongoActionExecutionRepository.cs` (TTL 30d)
   - `DependencyInjection.cs` — rejestracja + drop old zones hosted service

4. **ML** (`src/SafeView.ML/` lub `src/SafeView.Cameras/`):
   - `Detection/DetectionPipeline.cs` (zastępuje `DetectionFrameObserver`)
   - `Detection/TriggerEvaluator.cs`
   - `Detection/ActionDispatcher.cs`
   - `Detection/Handlers/LogAlertHandler.cs`, `InAppNotificationHandler.cs`
   - `Detection/Geometry/RoiCrop.cs` (prosty prostokątny crop bez SAHI)
   - `Detection/Geometry/BboxRuleEvaluator.cs` (CenterInZone / AnyCorner / AllCorners / NCorners / Iou)

5. **Web** (`src/SafeView.Web/`):
   - `Components/Pages/Rois.razor` + `RoiDialog.razor`
   - `Components/Pages/Triggers.razor` + `TriggerDialog.razor`
   - `Components/Pages/Actions.razor` + `ActionDialog.razor`
   - `Components/Pages/ActionHistory.razor`
   - `Components/Pages/Zones.razor` — refactor (select ROI, multi-select Triggers)
   - `Components/Pages/ZoneEditorDialog.razor` — refactor (walidacja geometrii)
   - `Components/Layout/NavMenu.razor` — nowe linki
   - `Resources/SharedResource.*.resx` — ~40 nowych kluczy

6. **Build + restart**
   - Verify `dotnet build` 0 errors
   - User robi `sudo ./restart.sh`
   - Po starcie: kolekcja `zones` wyczyszczona, user re-definiuje

## 11. Ryzyka i zabezpieczenia

| Ryzyko | Mitygacja |
|--------|-----------|
| Drop kolekcji zones = utrata danych | User wyraźnie zaakceptował. Komunikat w UI po starcie. |
| Mój kod ma bugi w BboxRuleEvaluator | Unit testy dla każdego trybu (CenterInZone, AnyCorner, AllCorners, NCorners, Iou) |
| Nieodpowiednie wywołanie actions (flood) | Cooldown + PersistentFrames + RateLimitPerMinute — trzy warstwy |
| VLLM hallucinations w Fazie 3 | JSON Schema validation + fallback (jak invalid response → Action jak przy confirmed=false) |
| Performance degraduje przy wielu ROI/modeli | Monitoring w Fazie 2 (inference time per ROI), `system_events` warnings |

---

**Koniec dokumentu designu.** Następny krok: implementacja Fazy 1 zgodnie z sekcją 10.
