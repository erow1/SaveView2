# Bundled ML Models — SafeView BHP Out-of-Box

Modele ML do detekcji obiektów na klatkach z kamer. **Pliki ONNX gitignored** (zbyt duże na repo).
Pobierz jednorazowo:

```bash
./scripts/download-models.sh
```

To pobiera **defaultowy zestaw BHP** (~250 MB łącznie):
- `yolov8n-coco` — general detection (osoby, pojazdy, ...)
- `yolov8s-coco` — większy general (lepsza dokładność)
- `yolov8n-ppe` — **PPE detection** (kask/maska/kamizelka/etc) ⭐
- `yolov8s-fire-smoke` — **fire/smoke detection** ⭐
- `owlv2-base` — **open-vocab text prompts** (dowolne klasy przez prompty) ⭐

## Modele w pełnej gamie

### COCO general (Apache 2.0, Ultralytics)

| Folder | Variant | Klasy | Rozmiar | Źródło |
|---|---|---|---|---|
| `yolov8n-coco` | nano (3M params) | 80 COCO | 12 MB | ultralytics CLI |
| `yolov8s-coco` | small (11M) | 80 COCO | 44 MB | ultralytics CLI |
| `yolov8m-coco` | medium (28M) | 80 COCO | 101 MB | ultralytics CLI (opcjonalny) |
| `yolov8l-coco` | large (48M) | 80 COCO | 167 MB | ultralytics CLI (opcjonalny) |

COCO klasy: person, car, truck, bus, bicycle, motorcycle, ... (80 ogółem). Wózek widłowy = `truck`. Brak gaśnic / kasków.

### BHP specialty fine-tunes ⭐

| Folder | Klasy | Rozmiar | Źródło | Use case |
|---|---|---|---|---|
| `yolov8n-ppe` | **Hardhat, Mask, NO-Hardhat, NO-Mask, NO-Safety Vest, Person, Safety Cone, Safety Vest, machinery, vehicle** | 12 MB | `Hansung-Cho/yolov8-ppe-detection` (HF, MIT) | Wykrywanie czy pracownicy mają PPE (i którzy NIE mają) |
| `yolov8s-fire-smoke` | fire, other, smoke | 43 MB | `Mehedi-2-96/fire-smoke-detection-yolo` (HF) | Wczesne wykrywanie pożaru / dymu |

**Tip detection rules**: dla PPE używaj klas `NO-Hardhat`, `NO-Mask`, `NO-Safety Vest` jako trigger condition — bezpośrednio mówi "ktoś jest bez ochrony". Klasy pozytywne (`Hardhat`, `Safety Vest`) służą do walidacji że detekcja działa, nie jako alarm.

### Open-vocabulary (text prompts)

| Folder | Backend | Rozmiar | Speed | Use case |
|---|---|---|---|---|
| `owlv2-base` | OwlV2 (Google, Apache 2.0) | 614 MB | szybki | Dowolne klasy przez prompty tekstowe ("hard hat", "fire extinguisher", "person climbing") |
| `owlv2-large` | OwlV2 large patch14 | 1.74 GB | 3-4× wolniejszy | Najlepsze pokrycie rzadkich klas — produkcja jakościowa |

OWLv2 jest fundamentem fallback-em: jeśli żaden specjalny YOLO nie ma klasy której potrzebujesz (np. "rozlanie cieczy", "iskry spawalnicze", "łańcuch palet upadający"), wpisujesz prompt i OWLv2 spróbuje. Nie zawsze idealnie ale szeroki zasięg.

### Visual prompts (currently unavailable)

YOLOE wymaga AGPL + custom export z `.safetensors` → niedostępny out-of-box. Ticket #57 follow-up jeśli potrzebny.

## Jak ModelSeeder rozpoznaje folder

`src/SafeView.Infrastructure/ML/ModelSeeder.cs` skanuje przy starcie i przypisuje Backend per folder structure:

| Co jest w folderze | Backend | Capabilities |
|---|---|---|
| `model.onnx + preprocessor_config.json + tokenizer/` | OwlV2 | TextPrompts |
| `model.onnx + text-encoder.onnx + image-encoder.onnx + tokenizer/` | YoloE | ClosedSet + TextPrompts + VisualPrompts |
| `model.onnx + labels.txt` (default) | Onnx | ClosedSet |

Modele są rejestrowane jako `Enabled=false` — user musi aktywować w `/models`.

## Pobieranie pojedynczego modelu

```bash
./scripts/download-models.sh yolov8n-ppe                 # PPE
./scripts/download-models.sh yolov8s-fire-smoke          # Fire/smoke
./scripts/download-models.sh owlv2-large                 # Większy OWLv2
./scripts/download-models.sh yolov8m-coco yolov8l-coco   # Większe COCO
```

## Custom fine-tune (jeśli specjalna klasa nie jest dostępna)

Jeśli żaden z bundled-em nie wykrywa Twojej specyficznej klasy (np. "katastrofa walizki firmowej z logo X"):

1. Zbierz ~200 zdjęć tej klasy (~30 oznaczone bbox-em)
2. Trenuj YOLOv8n na Roboflow / Ultralytics CLI
3. Wyeksportuj do ONNX:
   ```
   yolo export model=runs/detect/train/weights/best.pt format=onnx imgsz=640 dynamic=True
   ```
4. Zrób folder `runtime/models/twoja-nazwa/` + wstaw `model.onnx` + `labels.txt`
5. `sudo ./restart.sh` — ModelSeeder wykryje i zarejestruje

## Rozmiary i wydajność (orientacyjne)

| Model | Rozmiar | CPU inference (1080p) | Use case |
|---|---|---|---|
| yolov8n (any) | ~12 MB | ~25 ms | Dla 1-5 kamer @ 5s interval, wystarcza ~5% CPU |
| yolov8s (any) | ~44 MB | ~45 ms | Dla 5-15 kamer, ~10% CPU |
| owlv2-base | 614 MB | ~300-500 ms | Manual test detect; produkcja per-trigger |
| owlv2-large | 1.74 GB | ~1.5-2.5 s | Manual test detect; produkcja per-trigger (+VLLM dla pewności) |

OWLv2 jest dużo wolniejszy bo ViT-based + 960×960 input. Polecane: użyj YOLOv8 specialty (PPE / fire-smoke) jako primary detector w ROI, OWLv2 tylko ad-hoc gdy potrzebujesz testu nowej klasy.

## Wszystkie modele MIT/Apache 2.0 — komercyjnie OK

Wszystkie bundled modele mają **permissive licencje** (Apache 2.0 ultralytics + MIT Hansung-Cho + open-source Mehedi). Nie ma AGPL trap-ów. Możesz wdrażać u klientów komercyjnie bez ograniczeń.
