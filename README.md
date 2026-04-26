# SafeView

Enterprise computer-vision safety platform — Blazor Server + MongoDB + ONNX/Roboflow + vLLM.

Patrz: [`documentation/PLAN_PRAC.md`](documentation/PLAN_PRAC.md), [`documentation/ARCHITECTURE.md`](documentation/ARCHITECTURE.md), [`documentation/BACKLOG.md`](documentation/BACKLOG.md).

---

## Wymagania (środowisko developerskie)

| Składnik | Wersja | Uwagi |
|---|---|---|
| .NET SDK | **10.0+** | `dotnet --version` |
| MongoDB | 7.0+ | localhost:27017 (lokalna instalacja lub Docker) |
| FFmpeg | 6.0+ | w `PATH` — `ffmpeg -version` |
| MediaMTX | 1.9+ | sidecar gateway — `tools/mediamtx/` (pobierany osobno w fazie 2) |
| Node.js (opcjonalnie) | 20+ | tylko jeśli budujesz assety statyczne |

### Instalacja (macOS)
```sh
brew install dotnet mongodb-community ffmpeg
brew services start mongodb-community
```

### Instalacja (Linux — Debian/Ubuntu)
```sh
sudo apt install -y dotnet-sdk-10.0 ffmpeg
# MongoDB: zgodnie z instrukcjami mongodb.com/docs/manual/installation/
```

### Instalacja (Windows)
- .NET 10 SDK: https://dotnet.microsoft.com/download
- MongoDB: https://www.mongodb.com/try/download/community
- FFmpeg: https://www.gyan.dev/ffmpeg/builds/ (dodać do PATH)

---

## Build + Run

```sh
dotnet restore SafeView.slnx
dotnet build SafeView.slnx
dotnet run --project src/SafeView.Web
```

Aplikacja nasłuchuje domyślnie na `https://localhost:5001` / `http://localhost:5000`.

---

## Struktura solucji

```
SafeView.slnx
├── src/
│   ├── SafeView.Domain/          Encje, VO, domain events (bez zależności)
│   ├── SafeView.Application/     Use-cases, DTO, interfejsy (MediatR, FluentValidation)
│   ├── SafeView.Infrastructure/  MongoDB, file storage, scheduler
│   ├── SafeView.ML/              ONNX Runtime, pre/post-processing
│   ├── SafeView.LLM/             vLLM / Ollama / LM Studio clients + structured outputs
│   ├── SafeView.Cameras/         MediaMTX REST + FFmpeg wrapper + FrameSampler
│   ├── SafeView.Roboflow/        Klient HTTP dla Roboflow inference
│   ├── SafeView.Licensing/       AES-256-GCM + HMAC + walidator pliku .lic
│   ├── SafeView.Security/        Auth, RBAC, audit log, Argon2id
│   └── SafeView.Web/             Blazor Server (MudBlazor theme zgodny ze stroną)
├── tests/
│   ├── SafeView.Domain.Tests/
│   ├── SafeView.Application.Tests/
│   ├── SafeView.ML.Tests/
│   └── SafeView.Web.Tests/
├── tools/
│   └── SafeView.LicenseGenerator/  CLI do generowania plików licencji
└── documentation/                  Plan, architektura, backlog, webpage (makieta stylu)
```

Zależności projektów (Clean Architecture — Domain nic nie importuje):
```
Web → {Infrastructure, ML, LLM, Cameras, Roboflow, Licensing, Security} → Application → Domain
```

---

## Konwencje

- **Centralne wersje pakietów**: `Directory.Packages.props` — PackageReference bez `Version="..."`.
- **Globalny build-config**: `Directory.Build.props` — `TargetFramework=net10.0`, `Nullable=enable`, `TreatWarningsAsErrors=true`.
- **Format solucji**: `.slnx` (XML, .NET 10 default).
- **i18n**: wszystkie stringi UI przez `IStringLocalizer<T>` + `Resources/*.{pl,en}.resx`.
- **Konfiguracja**: `appsettings.json` + `appsettings.Development.json` (w .gitignore) + `appsettings.Secrets.json` (w .gitignore).

## Licencje modułów

Licencja (`.lic`) zawiera listę aktywnych modułów. Brak modułu = nierejestrowany w DI, niewidoczny w UI. Generator: `dotnet run --project tools/SafeView.LicenseGenerator -- issue ...`.

## Obecny status (2026-04-14)

✅ Faza 0 — scaffolding solucji (15 projektów, build zielony)
⏳ Faza 0 — DI + Serilog + MongoDB + MudBlazor theme
⏳ Faza 1 — Licencje + Auth + RBAC + i18n + UI
