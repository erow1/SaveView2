# Bundled Runtime Binaries

Ten katalog zawiera binarki **ffmpeg** i **mediamtx** dla wszystkich obsługiwanych platform SafeView. Aplikacja używa ich domyślnie — bez konieczności instalowania ffmpeg/mediamtx w systemie.

## Struktura

```
runtime/binaries/
  osx-arm64/        Apple Silicon (M1/M2/M3/M4)
    ffmpeg
    mediamtx
  linux-x64/        Linux amd64 (serwery)
    ffmpeg
    mediamtx
  win-x64/          Windows 64-bit
    ffmpeg.exe
    mediamtx.exe
```

## Jak pobrać

Binarki **nie są commitowane do gita** (ok. 400 MB). Po sklonowaniu repo uruchom:

```bash
./scripts/download-binaries.sh
```

Skrypt pobierze najnowsze wersje:
- **MediaMTX** z https://github.com/bluenviron/mediamtx/releases/latest
- **FFmpeg**:
  - macOS: https://evermeet.cx/ffmpeg/
  - Linux: https://johnvansickle.com/ffmpeg/ (static build)
  - Windows: https://www.gyan.dev/ffmpeg/builds/ (release essentials)

Aby pobrać tylko dla jednej platformy:
```bash
./scripts/download-binaries.sh osx-arm64
```

## Jak aplikacja je wybiera

`SafeView.Cameras.BundledBinaries` rozwiązuje właściwą ścieżkę na podstawie
`System.Runtime.InteropServices.RuntimeInformation` — wybiera katalog
odpowiadający aktualnej platformie/architekturze.

Można nadpisać przez konfigurację:
- `Ffmpeg:BinaryPath` — absolutna ścieżka do ffmpeg
- `MediaMtx:BinaryPath` — absolutna ścieżka do mediamtx

Gdy te opcje są puste, aplikacja używa bundled binaries z tego katalogu.
Jeśli ani bundled, ani systemowy binary nie jest dostępny — aplikacja zaloguje
ostrzeżenie i operacje wymagające ffmpeg/mediamtx zawiodą.
