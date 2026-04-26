# SafeView — Backlog zadań

> Zadania rozbite na fazy zgodnie z `PLAN_PRAC.md` v0.3.
> Każde zadanie = atomiczny issue do zamknięcia.

---

## Faza 0 — Fundament

- [x] **F0-1**: Utwórz strukturę katalogów `src/`, `tests/`, `tools/`
- [ ] **F0-2**: Utwórz solucję `SafeView.sln` (.NET 10)
- [ ] **F0-3**: Utwórz projekty:
  - `SafeView.Domain` (classlib)
  - `SafeView.Application` (classlib)
  - `SafeView.Infrastructure` (classlib)
  - `SafeView.ML` (classlib)
  - `SafeView.LLM` (classlib)
  - `SafeView.Cameras` (classlib)
  - `SafeView.Roboflow` (classlib)
  - `SafeView.Licensing` (classlib)
  - `SafeView.Security` (classlib)
  - `SafeView.Web` (Blazor Server)
- [ ] **F0-4**: Ustaw `Directory.Build.props` (Nullable, TreatWarningsAsErrors, LangVersion latest)
- [ ] **F0-5**: Dodaj referencje między projektami (Clean Architecture direction)
- [ ] **F0-6**: Dodaj Serilog (Console, File, MongoDB sink)
- [ ] **F0-7**: Dodaj MongoDB.Driver + konwencje (camelCase, ignoreExtra)
- [ ] **F0-8**: Dodaj MudBlazor + theme dark/yellow zgodny ze stroną
- [ ] **F0-9**: Dodaj strukturę `Resources/` + `IStringLocalizer` (PL/EN)
- [ ] **F0-10**: Projekty testowe (`*.Tests` dla Domain, Application, ML, Web) + xUnit + FluentAssertions
- [ ] **F0-11**: `README.md` dla developera (setup lokalny: MongoDB, MediaMTX, FFmpeg)

## Faza 1 — Bezpieczeństwo + Licencje

- [ ] **F1-1**: `SafeView.Licensing` — model `LicenseFile`, `LicenseModule`, `LicenseLimits`
- [ ] **F1-2**: `LicenseCryptoService` — AES-256-GCM + HMAC-SHA256 + PBKDF2 masterKey obfuscated
- [ ] **F1-3**: `ILicenseService` — Load, Validate, IsModuleEnabled, GetLimit
- [ ] **F1-4**: `LicenseMonitor` (`IHostedService`) — walidacja co 1h
- [ ] **F1-5**: `tools/SafeView.LicenseGenerator` — CLI (issue, show, verify)
- [ ] **F1-6**: `IFeatureModule` interfejs + `FeatureModuleLoader` (warunkowa rejestracja w DI)
- [ ] **F1-7**: ASP.NET Core Identity na MongoDB (custom UserStore/RoleStore)
- [ ] **F1-8**: Argon2id (`Konscious.Security.Cryptography`) zamiast PBKDF2
- [ ] **F1-9**: RBAC — `Permission`, `Role`, mapowanie, Policy-based authorization
- [ ] **F1-10**: `IAuditLogger` + middleware logujący admin actions
- [ ] **F1-11**: Blazor login page + logout + zmiana hasła
- [ ] **F1-12**: Strona "Użytkownicy i role" (MudBlazor Data Grid + CRUD)
- [ ] **F1-13**: Culture middleware + przełącznik języka w nawigacji (PL/EN)
- [ ] **F1-14**: Strona "Licencja" (info o kliencie, module, limits, expiresAt)
- [ ] **F1-15**: Banner "tryb read-only" gdy licencja niewalidna

## Faza 2 — Kamery + Storage

- [ ] **F2-1**: Pobranie/bundling MediaMTX do `tools/mediamtx/`
- [ ] **F2-2**: `IMediaGateway` — wrapper REST MediaMTX (paths CRUD)
- [ ] **F2-3**: `FfmpegService` — wrapper `FFMpegCore` (snapshot, clip, probe)
- [ ] **F2-4**: `FrameSampler` (`IHostedService`) per-kamera
- [ ] **F2-5**: `IFileStore` + `LocalDiskFileStore` (config ścieżek per-typ)
- [ ] **F2-6**: Encja `Camera` + CRUD w Application + MongoDB repo
- [ ] **F2-7**: Strona "Kamery" (lista, dodaj/edytuj, test połączenia)
- [ ] **F2-8**: Strona "Live View" (grid HLS via MediaMTX + overlay SVG)
- [ ] **F2-9**: Rolling buffer ostatnich 10s per-kamera (do clipów incydentów)

## Faza 3 — ML Core (MVP)

- [ ] **F3-1**: `IObjectDetector` + `Frame`, `Detection`, `BoundingBox`
- [ ] **F3-2**: `OnnxObjectDetector` + auto-detect EP (CUDA/DirectML/CPU)
- [ ] **F3-3**: `RoboflowObjectDetector` (HTTP + auth)
- [ ] **F3-4**: Encja `MlModel` + upload `.onnx` + metadane (klasy, input shape)
- [ ] **F3-5**: Strona "Modele" (lista, upload, test inference)
- [ ] **F3-6**: `RuleEngine` — reguły deklaratywne (class + zone + threshold)
- [ ] **F3-7**: **MOD.PPE** — moduł `SafeView.Modules.Ppe` z defaultowymi regułami i klasami
- [ ] **F3-8**: Pipeline end-to-end: Kamera → FrameSampler → Detector → RuleEngine → Events → UI

## Faza 4 — Strefy

- [ ] **F4-1**: Encja `Zone` (poligon) + CRUD
- [ ] **F4-2**: Edytor stref (canvas + snapshot kamery jako tło)
- [ ] **F4-3**: **MOD.ZONES** — alerty wejścia/wyjścia, reguły kombinowane PPE×strefa
- [ ] **F4-4**: Encja `Incident` + agregacja eventów
- [ ] **F4-5**: Strona "Incydenty" (lista + szczegóły + klip video)

## Faza 5 — LLM (vLLM + structured outputs)

- [ ] **F5-1**: `ILlmClient` + `LlmPrompt` + `IncidentClassification` DTO
- [ ] **F5-2**: `VllmClient` (`guided_json` / `response_format`)
- [ ] **F5-3**: `OllamaClient`, `LmStudioClient`, `OpenAiCompatibleClient`
- [ ] **F5-4**: Encja `LlmEndpoint` + CRUD + test połączenia
- [ ] **F5-5**: Integracja LLM w pipeline incydentów (auto-klasyfikacja)
- [ ] **F5-6**: Strona "Asystent" (chat z RAG nad incydentami)

## Faza 6 — Analytics + Reports

- [ ] **F6-1**: **MOD.ANALYTICS** — dashboard KPI (ApexCharts)
- [ ] **F6-2**: Heatmapa aktywności per-kamera
- [ ] **F6-3**: **MOD.REPORTS** — generator PDF (QuestPDF)
- [ ] **F6-4**: Harmonogram raportów (Hangfire/Quartz)

## Faza 7 — Integracje

- [ ] **F7-1**: **MOD.API** — REST API dla integratorów + OpenAPI
- [ ] **F7-2**: WebSocket/SignalR endpoint zewnętrzny
- [ ] **F7-3**: **MOD.SIEM** — syslog + webhook + Splunk HEC
- [ ] **F7-4**: Notyfikacje: SMTP, SMS (abstrakcja `INotificationChannel`)

## Faza 8 — Hardening + dystrybucja

- [ ] **F8-1**: Testy obciążeniowe (16+ kamer równolegle)
- [ ] **F8-2**: Instalator Linux (deb/rpm + systemd unit)
- [ ] **F8-3**: Instalator Windows (MSI + Windows Service)
- [ ] **F8-4**: Instalator macOS (.pkg + launchd)
- [ ] **F8-5**: Dokumentacja administratora + user guide (PL/EN)
- [ ] **F8-6**: Skrypt upgrade / migracje schematu MongoDB
