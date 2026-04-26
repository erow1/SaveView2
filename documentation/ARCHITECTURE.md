# SafeView — Architektura systemu

> **Wersja:** 0.1
> **Data:** 2026-04-14
> **Powiązane:** `PLAN_PRAC.md` (v0.2)

---

## 1. Warstwy (Clean Architecture)

```mermaid
flowchart TB
    subgraph Presentation["Presentation Layer"]
        WEB[SafeView.Web<br/>Blazor Server + SignalR + i18n]
    end
    subgraph App["Application Layer"]
        APP[SafeView.Application<br/>Use-cases · MediatR · Validation · DTO]
    end
    subgraph Domain["Domain Layer (rdzeń)"]
        DOM[SafeView.Domain<br/>Entities · Aggregates · Value Objects · Domain Events]
    end
    subgraph Infra["Infrastructure Layer"]
        INF[SafeView.Infrastructure<br/>MongoDB · FileStore · Scheduler · Audit]
        ML[SafeView.ML<br/>ONNX Runtime · Pre/Post-proc]
        LLM[SafeView.LLM<br/>vLLM · Ollama · LMStudio · Structured Outputs]
        CAM[SafeView.Cameras<br/>MediaMTX · FFmpeg · Frame Sampler]
        RF[SafeView.Roboflow<br/>HTTP Client · Auth]
        LIC[SafeView.Licensing<br/>AES-GCM · HMAC · Validator]
        SEC[SafeView.Security<br/>Auth · RBAC · Audit]
    end
    subgraph Modules["Feature Modules (plugins — warunkowe po licencji)"]
        MP[Modules.PPE]
        MZ[Modules.Zones]
        MF[Modules.Falls]
        MC[Modules.Collision]
        MFIRE[Modules.Fire]
        MANA[Modules.Analytics]
        MREP[Modules.Reports]
        MLLM[Modules.LlmAssistant]
        MAPI[Modules.Api]
        MSIEM[Modules.Siem]
    end

    WEB --> APP
    APP --> DOM
    APP -.uses.-> INF
    APP -.uses.-> ML
    APP -.uses.-> LLM
    APP -.uses.-> CAM
    APP -.uses.-> RF
    APP -.uses.-> LIC
    APP -.uses.-> SEC
    Modules --> APP
    WEB -. registers .-> Modules
```

**Kierunek zależności:** wszystko wskazuje na Domain (Domain nic nie importuje). Infrastructure i Application implementują interfejsy zdefiniowane w Domain/Application.

---

## 2. Pipeline detekcji zdarzenia (runtime)

```mermaid
sequenceDiagram
    participant CAM as Kamera IP
    participant MTX as MediaMTX (sidecar)
    participant FS as Frame Sampler<br/>(FFmpeg snapshot)
    participant ML as IObjectDetector<br/>(ONNX / Roboflow)
    participant RE as Rule Engine
    participant LLM as ILlmClient<br/>(vLLM guided_json)
    participant DB as MongoDB
    participant DISK as File Store
    participant ALERT as Alert Dispatcher
    participant UI as Blazor UI (SignalR)

    CAM->>MTX: RTSP push
    MTX->>FS: HLS/RTSP relay
    loop every N seconds (konfig)
        FS->>FS: snapshot JPG (FFmpeg)
        FS->>DISK: zapis frame
        FS->>ML: Frame + CameraContext
        ML-->>FS: Detections[bbox, class, conf]
        FS->>RE: Detections + Zones + Policies
        alt zdarzenie istotne
            RE->>LLM: prompt + JsonSchema<IncidentClassification>
            LLM-->>RE: { severity, category, incidentType, summaryPl, summaryEn, ... }
            RE->>DB: save Incident
            RE->>DISK: save 10s clip (FFmpeg)
            RE->>ALERT: dispatch (email/SMS/webhook/SIEM)
            RE->>UI: push via SignalR
        else tylko event
            RE->>DB: save DetectionEvent
        end
    end
```

**Kluczowe:** LLM dostaje już odfiltrowane zdarzenia (Rule Engine robi pre-filtracja), nie wszystkie klatki. Structured output vLLM zwraca deterministyczny JSON, który bezpośrednio trafia do kolekcji `incidents`.

---

## 3. Flow licencji

```mermaid
flowchart LR
    START([Start aplikacji]) --> LOAD[Wczytaj .lic z dysku]
    LOAD --> DEC[AES-256-GCM decrypt<br/>klucz = PBKDF2 masterKey w kodzie]
    DEC --> VER{HMAC OK?}
    VER -- nie --> DEGRADE[Tryb read-only<br/>banner o niewalidnej licencji]
    VER -- tak --> EXP{expiresAt > now?}
    EXP -- nie --> DEGRADE
    EXP -- tak --> REG[DI: rejestruj moduły<br/>z listy 'modules']
    REG --> RUN([Aplikacja działa])
    RUN -.co 1h.-> LOAD
```

**Bezpieczeństwo:** masterKey jest "zaszyty" w kodzie (obfuskowany, składany z fragmentów) — to bariera przeciwko *casual tamperingowi*, nie przeciwko reverse-engineeringowi. Prawdziwa ochrona = prawny kontrakt + HMAC (klient nie może sam wygenerować licencji bez masterKey).

---

## 4. Konfiguracja MediaMTX

```mermaid
flowchart TB
    UI[UI: dodaj kamerę] --> APP[Application<br/>AddCameraCommand]
    APP --> MONGO[(MongoDB<br/>cameras)]
    APP --> MTX[MediaMTX REST API<br/>POST /v3/config/paths/add]
    MTX -->|pull z kamery| CAM[Kamera RTSP]
    SUB1[Blazor UI<br/>HLS player] -.subscribe.-> MTX
    SUB2[Frame Sampler<br/>FFmpeg] -.subscribe.-> MTX
    SUB3[Recording Service<br/>klipy 10s] -.subscribe.-> MTX
```

**Zysk:** kamera obsługuje *jedno* połączenie (MediaMTX pull), trzech konsumentów (UI, ML, recording) nie obciąża jej wielokrotnie.

---

## 5. Moduł licencjonowany — model pluginu

Każdy feature module to osobny assembly z klasą `IFeatureModule`:

```csharp
public interface IFeatureModule {
    string Code { get; }                          // np. "MOD.PPE"
    string NameKey { get; }                       // klucz i18n
    void RegisterServices(IServiceCollection s);  // wszystko co moduł potrzebuje
    void RegisterRoutes(IEndpointRouteBuilder e); // opcjonalne endpointy
    IEnumerable<NavItem> GetNavigation();         // pozycje menu
}
```

W `Program.cs` (rejestracja warunkowa):
```csharp
var license = await licenseService.LoadAsync();
foreach (var module in discoveredModules) {
    if (license.IsModuleEnabled(module.Code)) {
        module.RegisterServices(builder.Services);
    }
}
```

---

## 6. Struktura katalogów na dysku (konfigurowalne)

```
{StorageRoot}/
├── frames/{yyyy}/{MM}/{dd}/{cameraId}/{HHmmss}_{eventId}.jpg
├── clips/{yyyy}/{MM}/{dd}/{cameraId}/{HHmmss}_{incidentId}.mp4
├── reports/{yyyy}/{MM}/{reportId}.pdf
├── models/{modelId}/{version}.onnx
└── uploads/{userId}/{yyyy-MM}/{filename}
```

Wszystkie 5 ścieżek to osobne `IOptions<StoragePaths>` — admin może skierować `clips/` na szybszy dysk SSD, `reports/` na wolniejszy, itd.

---

## 7. Diagram komponentów — widok wdrożenia

```mermaid
flowchart TB
    subgraph Server["Serwer SafeView (Linux/Win/macOS)"]
        BLAZOR[SafeView.Web :5000]
        MTX[MediaMTX :8554/:8888]
        MONGO[(MongoDB :27017)]
        DISK[(Dysk: StorageRoot)]
    end
    subgraph ML_Host["Host GPU (opcjonalnie)"]
        VLLM[vLLM :8000]
        OLLAMA[Ollama :11434]
    end
    subgraph Cameras["Sieć kamer"]
        C1[Kamera 1 RTSP]
        C2[Kamera 2 RTSP]
        CN[Kamera N]
    end
    subgraph External["Zewnętrzne (opcjonalne)"]
        RF[Roboflow Inference]
        SIEM[SIEM / Syslog]
        SMTP[SMTP Relay]
    end
    subgraph Clients["Klienci"]
        BROWSER[Przeglądarka<br/>Blazor UI]
    end

    C1 -->|RTSP| MTX
    C2 -->|RTSP| MTX
    CN -->|RTSP| MTX
    MTX -->|HLS/WebRTC| BROWSER
    MTX -->|snapshot| BLAZOR
    BLAZOR <-->|CRUD| MONGO
    BLAZOR <-->|file paths| DISK
    BLAZOR -->|HTTP| VLLM
    BLAZOR -->|HTTP| OLLAMA
    BLAZOR -->|HTTP| RF
    BLAZOR -->|syslog/webhook| SIEM
    BLAZOR -->|SMTP| SMTP
    BLAZOR <-->|SignalR| BROWSER
```

---

## 8. Podsumowanie kluczowych decyzji technicznych

| Decyzja | Uzasadnienie |
|---|---|
| Blazor Server (nie WASM) | SignalR out-of-the-box, łatwy dostęp do lokalnych zasobów (ONNX, FFmpeg), brak problemów CORS/auth, prostsze debugowanie |
| MongoDB (nie SQL) | Schema-flexible (eventy detekcji mają różne struktury), łatwe skalowanie, dobra biblioteka `MongoDB.Driver` |
| MediaMTX (nie bezpośrednie RTSP) | Multiplexing strumieni, niezależność od kamer, łatwe udostępnienie HLS/WebRTC do przeglądarki |
| vLLM + guided_json | Deterministyczne structured outputs bez post-processingu, wysoka wydajność na GPU |
| Pluginy per-licencja | Klient płaci za to co używa; kod modułów niewczytywany jeśli wyłączony (DI + assembly scan) |
| Pliki na dysku, nie w DB | MongoDB to nie storage dla MB/GB plików; ścieżki + indeksy w DB, pliki na lokalnym/sieciowym FS |
