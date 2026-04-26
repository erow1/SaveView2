# SafeView — Plan Prac Projektowych

> **Wersja:** 0.3 (.NET 10 zamiast .NET 9 — decyzja z 2026-04-14)
> **Data:** 2026-04-14
> **Autor planu:** Claude (na podstawie analizy `/documentation` + `/documentation/webpage` + 3 PDF schematów blokowych)

## 0. Decyzje zaakceptowane (nadrzędne wobec sekcji 11)

| # | Pytanie | Decyzja |
|---|---|---|
| 1 | Wersja .NET | **.NET 10 LTS** (decyzja z 2026-04-14 — .NET 9 poza wsparciem od maja 2026) |
| 2 | Biblioteka UI | **MudBlazor** + custom theme zgodny ze stylem strony |
| 3 | Video ingest | **MediaMTX** (gateway RTSP/RTMP/HLS/WebRTC) + **FFmpeg** (transkodowanie/snapshoty) |
| 4 | MFA | **Później** (po MVP, przygotować abstrakcję ale nie implementować) |
| 5 | Auth | **Tylko lokalne na start** (Identity + MongoDB), bez OIDC |
| 6 | LLM | **vLLM** jako główny backend + Ollama/LM Studio jako alternatywa; **structured outputs** (JSON schema / guided generation) do kontekstowego wykrywania zdarzeń, wynik strukturalny do dalszego przetwarzania w pipeline |
| 7 | i18n | **Wielojęzyczność od początku**: PL + EN, mechanizm rozszerzalny (`.resx` / pliki JSON, `IStringLocalizer`) |
| 8 | Roboflow | **Od razu** — równolegle z ONNX, jako alternatywny backend inferencji już w MVP |
| 9 | Edge Agent | **Nie** — monolit na jednej maszynie |
| 10 | MVP use-case | **MOD.PPE + MOD.ZONES** (najszerzej cytowane w case-studies) |

### 0.1. Konsekwencje decyzji dla architektury

- **MediaMTX** wprowadza dodatkowy proces (gateway) — uruchamiany jako sidecar/usługa systemowa. Aplikacja SafeView konfiguruje MediaMTX (REST API) i konsumuje strumienie (HLS/RTSP/WebRTC). Dzięki temu wiele kamer można zrelay'ować raz, a SafeView + przeglądarka klienta + ML pipeline konsumują te same strumienie z lokalnego MediaMTX bez obciążania kamer.
- **vLLM ze structured outputs** — `ILlmClient` musi wspierać tryb `CompleteStructuredAsync<T>(prompt, jsonSchema)` i deserializować wynik do silnie typowanych obiektów (np. `IncidentClassification { Severity, Category, RecommendedAction }`). vLLM wspiera to natywnie przez `guided_json` / `guided_regex`.
- **Roboflow od razu** → Faza 3 (ML Core) musi już zawierać `RoboflowObjectDetector` jako równoprawną implementację `IObjectDetector`. Konfiguracja per-kamera: który backend użyć dla którego modelu.
- **i18n od początku** → wszystkie stringi UI przez `IStringLocalizer<T>`, brak hardcoded tekstów. Struktura: `Resources/SharedResources.pl.resx`, `Resources/SharedResources.en.resx` + `LocalizationOptions` + culture middleware. Wybór języka per-user (zapisany w profilu) + fallback na nagłówek `Accept-Language`.
- **MFA później** → w `IUserAuthenticator` zostawić hook (`RequiresSecondFactor`), implementacja zwraca `false` w MVP. Schema usera już zawiera pole `MfaSecret` (nullable) — nie trzeba migracji w przyszłości.

---

## 1. Cel projektu i kontekst

**SafeView** — system analityki obrazu z kamer przemysłowych, oparty o ML/LLM, wykrywający nieprawidłowości BHP i zagrożenia w środowisku produkcyjnym. Adresowany do dużych przedsiębiorstw przemysłowych (chemia, energetyka, oil&gas, hutnictwo, produkcja, logistyka).

### 1.1. Założenia twarde (z briefu użytkownika)

| Obszar | Decyzja |
|---|---|
| Stack | .NET Core + **Blazor Server** + DI |
| Hosting | Cross-platform: Linux / Windows / macOS |
| Architektura | SOLID, modułowa, łatwo rozszerzalna |
| Baza danych | **MongoDB** lokalna (localhost) |
| Pliki binarne | Na dysku, w konfigurowalnych katalogach; w bazie tylko ścieżki (NIE base64) |
| Licencjonowanie | Plik licencyjny szyfrowany kluczem zaszytym w kodzie; włącza/wyłącza moduły |
| Uprawnienia | Wielopoziomowy RBAC |
| ML lokalnie | **ONNX Runtime** (CPU/GPU), bez dostawców cloud (Anthropic/OpenAI **wykluczone**) |
| ML zewnętrznie | API do **Roboflow** (lub podobnych self-hosted) |
| LLM | Lokalne API: **Ollama**, **LM Studio**, kompatybilne |
| Styl UI | Zgodny z `/documentation/webpage` (dark + yellow `#FFD600`, Inter, JetBrains Mono) |

### 1.2. Co ujawniła analiza materiałów

- **Strona webowa** definiuje 3 produkty/warstwy: **Detect+Guard** (core CV), **Analytics** (dashboardy/heatmapy/raporty), **API** (REST+WebSocket+SDK).
- **6 case-studies branżowych** (chemia, energetyka, oil&gas, hutnictwo, produkcja, logistyka) — każde definiuje konkretne scenariusze detekcji (PPE, strefy zakazane, kolizje, upadki, ATEX, gorące powierzchnie itd.).
- **3 PDF Master Stack** = warstwy systemu: Edge Devices → Streaming/Ingest → Core ML → Analytics Engine → Integration/Output → Infrastructure.
- **Bezpieczeństwo**: TLS 1.3, AES-256-GCM, edge-first (raw video nie opuszcza klienta), GDPR/NIS2/AI Act, audit trail, RBAC, MFA.
- **Brak wzmianek o LLM** na stronie — to **nasze rozszerzenie** względem oryginalnej wizji (np. generowanie raportów, asystent analityka, klasyfikacja zdarzeń w języku naturalnym).

---

## 2. Architektura wysokopoziomowa

```
┌─────────────────────────────────────────────────────────────┐
│                    SafeView.Web (Blazor Server)             │
│   UI · Dashboard · Konfiguracja · Live View · Raporty       │
└──────────────────────────┬──────────────────────────────────┘
                           │ DI
┌──────────────────────────┴──────────────────────────────────┐
│              SafeView.Application (Use-Cases / CQRS-lite)    │
│  Services: Detection · Alerting · Reporting · Licensing      │
└────┬──────────┬──────────┬──────────┬──────────┬────────────┘
     │          │          │          │          │
┌────┴───┐ ┌────┴───┐ ┌────┴────┐ ┌───┴────┐ ┌───┴────────┐
│ Camera │ │  ML    │ │  LLM    │ │ Storage│ │  License   │
│ Module │ │ Module │ │ Module  │ │ Module │ │  Module    │
│ (RTSP/ │ │ (ONNX/ │ │(Ollama/ │ │ (Mongo+│ │ (encrypted │
│ ONVIF) │ │Roboflow│ │LMStudio)│ │  Disk) │ │   file)    │
└────────┘ └────────┘ └─────────┘ └────────┘ └────────────┘
                           │
              ┌────────────┴────────────┐
              │   SafeView.Domain       │
              │   Entities · VOs · Events│
              └─────────────────────────┘
```

### 2.1. Struktura solucji (proponowana)

```
SafeView.sln
├── src/
│   ├── SafeView.Domain/             # Encje, agregaty, value objects, domain events
│   ├── SafeView.Application/        # Use-cases, interfejsy, DTO, walidacja
│   ├── SafeView.Infrastructure/     # MongoDB, file storage, scheduler
│   ├── SafeView.ML/                 # ONNX Runtime, abstrakcja modeli, GPU/CPU
│   ├── SafeView.LLM/                # Klienty: vLLM (+structured outputs), Ollama, LM Studio, OpenAI-compat
│   ├── SafeView.Cameras/            # MediaMTX konfigurator + FFmpeg snapshoty + frame sampler
│   ├── SafeView.Roboflow/           # Klient API Roboflow / self-hosted inference servers
│   ├── SafeView.Licensing/          # Generator + walidator pliku licencyjnego (AES)
│   ├── SafeView.Security/           # Auth, RBAC, audit log, hashing
│   ├── SafeView.Modules.*/          # Pluginy modułów funkcjonalnych (PPE, Zones, Falls...)
│   └── SafeView.Web/                # Blazor Server (UI + endpoints + SignalR)
├── tests/
│   ├── SafeView.Domain.Tests/
│   ├── SafeView.Application.Tests/
│   ├── SafeView.ML.Tests/
│   └── SafeView.Web.Tests/
├── tools/
│   └── SafeView.LicenseGenerator/   # CLI do generowania plików .lic
└── docs/
```

### 2.2. Kluczowe wzorce

- **Clean Architecture / Onion**: Domain → Application → Infrastructure → Web.
- **Dependency Injection**: wszystko przez interfejsy (`IModelInference`, `ILlmClient`, `ICameraStream`, `IFileStore`, `ILicenseService`).
- **Strategy Pattern** dla ML (lokalny ONNX vs Roboflow vs zewnętrzny endpoint).
- **Plugin Pattern** dla modułów funkcjonalnych — każdy moduł = osobne assembly, ładowane warunkowo na podstawie licencji.
- **MediatR (lub własny lekki bus)** — komendy/zapytania/eventy domenowe.
- **Background Services** (`IHostedService`) — pętle ingestu kamer, kolejki inference, scheduler raportów.

---

## 3. Moduły funkcjonalne (mapowane na licencjonowanie)

Każdy moduł = osobny *flag* w pliku licencyjnym. Brak flagi → moduł niewidoczny w UI i niezarejestrowany w DI.

| Kod modułu | Nazwa | Zakres |
|---|---|---|
| `CORE` | Core (zawsze włączony) | Auth, RBAC, konfiguracja, podgląd kamer |
| `MOD.PPE` | Detekcja PPE | Hełmy, kamizelki, okulary, rękawice, uprzęże |
| `MOD.ZONES` | Strefy zakazane / kontrola dostępu | Definicja stref, alerty wejścia |
| `MOD.FALLS` | Detekcja upadków / niezdolności | Pose estimation, bezruch, alarm SOS |
| `MOD.COLLISION` | Kolizje pojazd-człowiek | Wózki widłowe, AGV, dead-zones |
| `MOD.FIRE` | Detekcja ognia / dymu / wycieków | Klasyfikacja anomalii wizyjnych |
| `MOD.THERMAL` | Analiza termiczna | Kamery termowizyjne, gorące powierzchnie |
| `MOD.ATEX` | Strefy ATEX | Specjalne PPE, wyposażenie iskrobezpieczne |
| `MOD.ANALYTICS` | Dashboardy + heatmapy | Real-time + historyczne |
| `MOD.REPORTS` | Raporty PDF/CSV + harmonogram | Generator raportów |
| `MOD.LLM` | Asystent LLM | Streszczenia incydentów, Q&A nad zdarzeniami, klasyfikacja NL |
| `MOD.API` | REST API zewnętrzne + WebSocket | SDK / integracje |
| `MOD.SIEM` | Integracja SIEM / alarmy zewnętrzne | Splunk, BMS, syreny |
| `MOD.ROBOFLOW` | Backend Roboflow | Inference przez API |
| `MOD.CUSTOM_MODELS` | Własne modele klienta | Upload + zarządzanie modelami ONNX |

---

## 4. Model danych (MongoDB — zarys kolekcji)

| Kolekcja | Treść |
|---|---|
| `users` | Konta, hash hasła, role, MFA |
| `roles` | Role + uprawnienia (RBAC) |
| `auditLog` | Wszystkie operacje administracyjne (kto/co/kiedy) |
| `cameras` | Definicje kamer (RTSP URL, lokalizacja, strefa, modele przypisane) |
| `zones` | Strefy geometryczne (poligon na frame), poziom ryzyka |
| `models` | Metadane modeli ONNX (ścieżka pliku, wersja, klasy, wejście/wyjście) |
| `llmEndpoints` | Konfiguracja endpointów LLM (URL, model, system prompt) |
| `detectionEvents` | Zdarzenia detekcji (timestamp, kamera, klasy, bbox, ścieżki do klipów) |
| `incidents` | Zagregowane incydenty (powiązane eventy, status, przypisanie) |
| `alerts` | Wysłane alerty (kanał, status, próby) |
| `reports` | Wygenerowane raporty (harmonogram, ścieżka pliku) |
| `licenseInfo` | Cache zwalidowanej licencji (tylko do odczytu, źródłem prawdy plik .lic) |
| `settings` | Konfiguracja runtime (ścieżki katalogów, retencja, progi) |

**Pliki na dysku** (struktura konfigurowalna):
```
{StorageRoot}/
├── frames/          # Zrzuty klatek z detekcji (jpg)
├── clips/           # Krótkie video z momentu zdarzenia (mp4, ~10s)
├── reports/         # Wygenerowane PDF/CSV
├── models/          # Pliki .onnx
└── uploads/         # Pliki wgrane przez użytkownika
```

---

## 5. Mechanizm licencji

- **Format pliku `.lic`**: JSON zaszyfrowany **AES-256-GCM**, klucz + IV pochodne z `PBKDF2(masterKey, salt)` — `masterKey` zaszyty w kodzie (obfuskowany, świadomi że to nie jest kryptograficznie odporne — to ochrona przed casual tamperingiem, nie reverse-engineeringiem).
- **Zawartość**:
  ```json
  {
    "customerId": "...",
    "customerName": "...",
    "issuedAt": "2026-04-14",
    "expiresAt": "2027-04-14",
    "modules": ["CORE", "MOD.PPE", "MOD.ZONES", "MOD.LLM"],
    "limits": { "cameras": 32, "users": 50 },
    "signature": "<HMAC-SHA256>"
  }
  ```
- **Walidacja**: na starcie aplikacji + cyklicznie (`IHostedService` co 1h). Niewalidna licencja → tryb read-only z banerem.
- **CLI `SafeView.LicenseGenerator`**: narzędzie do generowania/odczytu plików (do użytku wewnętrznego).
- **Service**: `ILicenseService` — `IsModuleEnabled(string code)`, `GetLimit(string key)`, `GetCustomerInfo()`.
- **Plugin loader**: w `Program.cs` rejestracja modułu warunkowa (`if (license.IsModuleEnabled("MOD.PPE")) services.AddPpeModule();`).

---

## 6. Warstwa ML/LLM

### 6.1. ML — abstrakcja
```csharp
public interface IObjectDetector {
    Task<DetectionResult> DetectAsync(Frame frame, CancellationToken ct);
    string ModelId { get; }
}
```
Implementacje: `OnnxObjectDetector` (lokalny), `RoboflowObjectDetector` (HTTP), `GenericHttpDetector` (custom endpoint).

- **ONNX Runtime**: `Microsoft.ML.OnnxRuntime` + opcjonalnie `Microsoft.ML.OnnxRuntime.Gpu` (CUDA) lub `OnnxRuntime.DirectML` (Win/AMD).
- **Auto-detect provider**: na starcie aplikacja sprawdza dostępne EP (CUDA → DirectML → CPU).
- **Wsparcie modeli**: YOLO v8/v11, RT-DETR, własne (klasy + anchors definiowane w metadanych).
- **Pre/post-processing**: pluginowalne (różne layouty wejścia, NMS).

### 6.2. LLM — abstrakcja
```csharp
public interface ILlmClient {
    Task<string> CompleteAsync(LlmPrompt prompt, CancellationToken ct);
    IAsyncEnumerable<string> StreamAsync(LlmPrompt prompt, CancellationToken ct);

    // Structured outputs — kluczowe dla pipeline detekcji zdarzeń
    Task<T> CompleteStructuredAsync<T>(LlmPrompt prompt, JsonSchema schema, CancellationToken ct);
}
```
Implementacje:
- **`VllmClient`** — główny backend produkcyjny; używa `guided_json` / `response_format` vLLM dla deterministycznej struktury odpowiedzi.
- `OllamaClient` — rozwój/developer mode, wspiera `format: "json"` i schema w nowych wersjach.
- `LmStudioClient` — OpenAI-compatible, wspiera `response_format`.
- `OpenAiCompatibleClient` — generyczny fallback.

**Use-cases LLM (ze structured outputs)**:
1. **Kontekstowa klasyfikacja zdarzeń** — ML zwraca surowe bboxy/klasy, LLM dostaje kontekst (kamera, strefa, pora, historia) i zwraca:
   ```json
   { "severity": "HIGH", "category": "PPE_VIOLATION", "incidentType": "MISSING_HELMET_IN_HOT_ZONE",
     "requiresImmediateAction": true, "suggestedResponders": ["HSE_SHIFT_LEAD"], "summaryPl": "...", "summaryEn": "..." }
   ```
   Wynik trafia do `incidents` i bezpośrednio steruje routingiem alertów.
2. Streszczenia dziennych raportów BHP (PL/EN zgodnie z lokalizacją użytkownika).
3. Q&A nad bazą incydentów — naturalne pytania w UI ("ile naruszeń PPE w hali B w ostatnim tygodniu?").
4. Generowanie opisów incydentów do raportów PDF.

**Pipeline zdarzenia**:
```
Kamera → Frame → ONNX/Roboflow (detekcja) → Rule Engine (wstępna filtracja)
  → LLM structured (klasyfikacja + severity + opis) → Incident → Alert/Report
```

---

## 7. Bezpieczeństwo

- **Auth**: ASP.NET Core Identity (custom store na MongoDB) + opcjonalnie OIDC/SAML w przyszłości.
- **MFA**: TOTP (np. `Otp.NET`).
- **RBAC**: role konfigurowalne, uprawnienia per-moduł i per-akcja (`view:cameras`, `edit:zones`, `admin:users`).
- **Audit log**: middleware loguje wszystkie zmiany konfiguracji + dostępy administracyjne.
- **Hash haseł**: Argon2id (`Konscious.Security.Cryptography`).
- **Sekrety**: `appsettings.Secrets.json` poza repo + opcjonalnie integracja z Vault.
- **HTTPS**: wymuszone, w devie self-signed.
- **CSP, anti-forgery, rate limiting** — standard ASP.NET Core.

---

## 8. UI / UX (Blazor)

### 8.1. Struktura nawigacji
- **Dashboard** — KPI, mapa kamer, ostatnie alerty, heatmapa
- **Live View** — siatka kamer z nakładkami detekcji (real-time przez SignalR)
- **Incydenty** — lista, filtry, szczegóły, klipy wideo
- **Kamery** — CRUD, test połączenia, przypisanie modeli
- **Strefy** — edytor poligonów na klatce
- **Modele** — biblioteka modeli ONNX, upload, metadata
- **Raporty** — generator + harmonogram
- **LLM** — asystent (chat) + konfiguracja endpointów
- **Użytkownicy & Role** — admin
- **Ustawienia** — ścieżki, retencja, integracje, licencja
- **Audit Log** — przeglądarka

### 8.2. Styl
- **Paleta**: tło `#0A0A0A`/`#111111`, accent `#FFD600`, tekst `#FFFFFF`/`#AAAAAA`, granice `#FFFFFF12`.
- **Fonty**: Inter (UI), JetBrains Mono (metryki, kod, timestamps).
- **Komponenty**: glassmorphism (semi-transparentne karty + blur), zaokrąglenia 8/12/20px, animacje hover (lift 2px + shadow).
- **Biblioteka**: rozważyć **MudBlazor** lub **Radzen.Blazor** jako bazę + custom theme. Alternatywa: czyste Blazor + własny CSS (zgodny z `/webpage/styles.css`).
- **Wykresy**: ApexCharts.Blazor lub ChartJs.Blazor.
- **Live video**: HLS/MJPEG przez element `<video>`/`<img>`, overlay SVG dla bbox.

---

## 9. Plan wdrożenia (etapy)

### Faza 0 — Fundament (przygotowanie)
- [ ] Utworzenie solucji + struktura projektów (Clean Architecture)
- [ ] Konfiguracja DI, logowania (Serilog), konfiguracji (`IOptions`)
- [ ] Setup MongoDB (klient + konwencje, indeksy)
- [ ] Setup CI lokalnego (skrypty build/test)
- [ ] Bazowy theme Blazor (paleta, fonty, layout)

### Faza 1 — Bezpieczeństwo + Licencje
- [ ] Mechanizm licencji (AES + HMAC, walidator)
- [ ] CLI generator licencji
- [ ] Auth + RBAC (Identity na MongoDB)
- [ ] Audit log
- [ ] Strona logowania, zarządzanie userami/rolami

### Faza 2 — Kamery + Storage (MediaMTX + FFmpeg)
- [ ] Setup **MediaMTX** jako sidecar (docker/systemd/win-service) + konfiguracja przez REST API
- [ ] Abstrakcja `ICameraStream` + `IMediaGateway` (wrapper na MediaMTX)
- [ ] **FFmpeg** — snapshoty klatek (konfigurowalny FPS dla inference) + klipy incydentów (~10s rolling buffer)
- [ ] `IFileStore` (zapis frames/clips na dysku z konfigurowalnymi ścieżkami)
- [ ] CRUD kamer w UI + test połączenia (dodanie kamery → rejestracja path w MediaMTX)
- [ ] Live View w Blazor przez HLS/WebRTC z MediaMTX + overlay SVG

### Faza 3 — ML Core (ONNX + Roboflow równolegle) — **MVP**
- [ ] Abstrakcja `IObjectDetector` + implementacja `OnnxObjectDetector`
- [ ] **`RoboflowObjectDetector`** — od razu w MVP, wybór backendu per-model w konfiguracji
- [ ] Auto-detect CPU/GPU/DirectML, fallback
- [ ] Pipeline: kamera (MediaMTX) → frame (FFmpeg snapshot) → detector → eventy domenowe
- [ ] CRUD modeli + upload pliku ONNX + konfiguracja Roboflow workspace/endpoint
- [ ] **MOD.PPE** (wykrywanie hełmów/kamizelek/okularów) — **MVP scope**

### Faza 4 — Strefy + Detekcje zaawansowane
- [ ] Edytor stref (poligon na canvas) — **MOD.ZONES** w **MVP scope**
- [ ] Reguły strefowe: kombinacja PPE × strefa (np. brak hełmu w strefie X = HIGH)
- [ ] **MOD.FALLS** (pose estimation, bezruch)
- [ ] **MOD.COLLISION**, **MOD.FIRE** (kolejne moduły)
- [ ] **MOD.CUSTOM_MODELS** — workflow uploadu i wersjonowania własnych modeli

### Faza 5 — LLM (vLLM + structured outputs)
- [ ] `VllmClient` z `guided_json` jako podstawa
- [ ] `OllamaClient`, `LmStudioClient`, `OpenAiCompatibleClient` jako alternatywy
- [ ] **MOD.LLM**: kontekstowa klasyfikacja zdarzeń (output strukturalny → `incidents`)
- [ ] Asystent chat (Q&A nad incydentami) + streszczenia raportów
- [ ] CRUD endpointów LLM w UI + test połączenia + wybór modelu per-zadanie

### Faza 7 — Analytics + Reports
- [ ] **MOD.ANALYTICS** — dashboardy, heatmapy, trendy
- [ ] **MOD.REPORTS** — generator PDF (QuestPDF), CSV, harmonogram

### Faza 8 — Integracje
- [ ] **MOD.API** — REST + WebSocket dla integratorów
- [ ] **MOD.SIEM** — webhooki, syslog, alarmy zewnętrzne
- [ ] Notyfikacje: email (SMTP), SMS (pluginowalne providery)

### Faza 9 — Hardening + dystrybucja
- [ ] Testy obciążeniowe (wiele kamer równolegle)
- [ ] Pakiety instalacyjne: Linux (deb/rpm/systemd), Windows (MSI/Service), macOS (dmg)
- [ ] Dokumentacja administratora + użytkownika
- [ ] Skrypt migracji/upgrade

---

## 10. Stack technologiczny — proponowane biblioteki

| Obszar | Wybór |
|---|---|
| Framework | **.NET 10 LTS**, Blazor Server |
| UI | **MudBlazor** + custom theme (dark + `#FFD600`) |
| i18n | `Microsoft.Extensions.Localization` + `IStringLocalizer` + `.resx` (PL/EN, rozszerzalne) |
| Video gateway | **MediaMTX** (sidecar) |
| Video processing | **FFmpeg** (snapshoty + klipy, przez `FFMpegCore`) |
| LLM main | **vLLM** (`guided_json`) + Ollama/LM Studio jako alternatywy |
| ORM Mongo | `MongoDB.Driver` (oficjalny, bez pełnego ORM) |
| Logging | Serilog + sinks (Console, File, MongoDB) |
| Auth | ASP.NET Core Identity (custom Mongo store) |
| Hashing | Konscious.Security.Cryptography (Argon2) |
| MFA | Otp.NET |
| Validation | FluentValidation |
| Mediator | MediatR |
| ONNX | Microsoft.ML.OnnxRuntime (+ .Gpu / .DirectML) |
| Video (lib) | FFMpegCore (wrapper na FFmpeg CLI) |
| PDF | QuestPDF |
| UI Components | MudBlazor (zaakceptowane) + custom CSS |
| Charts | ApexCharts.Blazor |
| Background jobs | Hangfire (Mongo storage) lub Quartz.NET |
| Testy | xUnit + FluentAssertions + Testcontainers (Mongo) |

---

## 11. Otwarte pytania do akceptacji

Przed rozpoczęciem kodowania proszę o decyzje w poniższych punktach:

1. **Wersja .NET**: czy **.NET 9** (najnowsze LTS-adjacent), czy **.NET 8 LTS** (stabilność produkcyjna)?
2. **Biblioteka komponentów UI**: **MudBlazor**, **Radzen**, czy własne komponenty od zera (więcej kontroli nad stylem strony)?
3. **Video ingest**: **FFMpegCore** (lekkie, wymaga FFmpeg w systemie) vs **Emgu.CV** (cięższe, ale OpenCV in-process)?
4. **MFA od początku** czy w późniejszej fazie?
5. **Autentykacja**: tylko lokalna na start, czy od razu przygotować pod OIDC (Keycloak)?
6. **Zakres LLM**: tylko asystent chat, czy też auto-streszczenia incydentów + klasyfikacja eventów?
7. **Język UI**: tylko PL, czy od razu i18n (PL/EN)?
8. **Roboflow**: integracja od fazy 5, czy wcześniej (jako alternatywa dla ONNX w MVP)?
9. **Czy chcesz, żebym wprowadził też warstwę „Edge Agent" (osobna mała aplikacja na maszynie z kamerą, która tylko streamuje/preprocessuje), czy MVP ma być monolitem na jednej maszynie?**
10. **Pierwszy use-case do zaimplementowania w MVP** (sugestia: detekcja PPE + strefy zakazane — najszerzej cytowane w case-studies)?

---

## 12. Co dalej

Po Twojej akceptacji planu (lub korektach):
1. Zatwierdzamy odpowiedzi na pytania z sekcji 11.
2. Tworzę **osobny dokument `ARCHITECTURE.md`** z rozpisaną architekturą + diagramy (Mermaid).
3. Tworzę **`BACKLOG.md`** rozbity na konkretne zadania (issue-able).
4. Przystępujemy do **Fazy 0** — scaffolding solucji.

---

*Plan jest świadomie obszerny — system jest duży i lepiej teraz wyłapać rozbieżności niż po kilkunastu fazach. Każdą sekcję można skrócić/rozszerzyć przed startem implementacji.*
