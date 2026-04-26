# Bundled ML Models

Ten katalog zawiera pretrained modele YOLO (ONNX) używane przez SafeView do detekcji
obiektów na klatkach z kamer.

## Struktura

```
runtime/models/
  yolov8n-coco/           YOLOv8 nano — COCO 80 klas (person, car, truck, forklift*, ...)
    model.onnx            binarny model (~12 MB)
    labels.txt            lista klas
    README.md             szczegóły modelu
  yolov8s-coco/           YOLOv8 small — bardziej dokładny niż nano (~44 MB)
    model.onnx
    labels.txt
  yolov8n-fire-smoke/     Fire/smoke detection — opcjonalny, "bring your own"
    (patrz poniżej jak pobrać)
  yolov8n-ppe/            PPE (hard hat, safety vest) — opcjonalny
    (patrz poniżej)
```

*) Uwaga: COCO ma klasy `person`, `truck`, `car`, `bus`, `bicycle`, `motorcycle`, itp. 
   **Nie ma osobnej klasy `forklift`** — wózki widłowe są zwykle wykrywane jako `truck`.
   Dla precyzyjnej detekcji wózków wymagany jest dedykowany model (patrz sekcja "Custom models").

## Jak pobrać modele COCO (automatycznie)

Modele **NIE są commitowane do gita** (łącznie ~60 MB). Pobierz jednorazowo po sklonowaniu repo:

```bash
./scripts/download-models.sh
```

Skrypt:
1. Tworzy `.venv` z Pythonem i instaluje `ultralytics` (~500 MB zależności, jednorazowo).
2. Pobiera `yolov8n.pt` + `yolov8s.pt` z oficjalnych releases Ultralytics.
3. Eksportuje je do formatu ONNX (rozmiar 640×640, opset 12).
4. Umieszcza w `runtime/models/yolov8{n,s}-coco/model.onnx`.
5. Zapisuje `labels.txt` z nazwami klas COCO.

Po pobraniu modele są automatycznie rejestrowane w bazie MongoDB przy starcie aplikacji
(patrz `ModelSeeder` w `SafeView.Infrastructure`).

## Modele dodatkowe (opcjonalne — bring your own)

Gotowe, darmowe, publicznie hostowane ONNX-y dla **fire/smoke** i **PPE** są rzadkie.
Najczęściej wymagają pretrainingu na Twoim własnym datasecie (np. z Roboflow Universe).

### Fire/Smoke detection

Polecane źródła gotowych modeli (`.pt`, wymagają konwersji do ONNX):

- **Hugging Face** `keremberke/yolov5n-v7-0` — wyszukaj `fire-smoke-detection`
- **Roboflow Universe** — np. `continental-fire-detection`
- **GitHub** `Abonia1/Fire-Detection-YoloV8` — pretrained na ~5000 zdjęć

**Instrukcja**:
```bash
# 1. Pobierz .pt np. z GitHub:
curl -L -o runtime/models/yolov8n-fire-smoke/best.pt \
    "https://github.com/Abonia1/Fire-Detection-YoloV8/raw/main/best.pt"

# 2. Konwertuj do ONNX (używa tego samego venv co główny skrypt):
.venv/bin/yolo export model=runtime/models/yolov8n-fire-smoke/best.pt format=onnx imgsz=640

# 3. Zmień nazwę:
mv runtime/models/yolov8n-fire-smoke/best.onnx \
   runtime/models/yolov8n-fire-smoke/model.onnx

# 4. Utwórz labels.txt (zwykle 2 klasy):
printf "fire\nsmoke\n" > runtime/models/yolov8n-fire-smoke/labels.txt
```

Po restarcie aplikacji `ModelSeeder` automatycznie zarejestruje nowy model w bazie.

### PPE (Personal Protective Equipment) — hard hat, safety vest

Polecane źródła:

- **Roboflow Universe** `hard-hat-workers` (public dataset, v7+)
- **Kaggle** `andrewmvd/hard-hat-detection`
- **GitHub** `wiomoc/ppe-detection` — pretrained YOLOv5

Typowe klasy: `helmet`, `no_helmet`, `vest`, `no_vest`, `person`.

Instrukcja jak wyżej — pobierz `.pt`, skonwertuj, umieść w `yolov8n-ppe/model.onnx`,
wpisz klasy do `labels.txt`.

## Struktura pliku model

Każdy model musi mieć:

```
runtime/models/{nazwa}/
  model.onnx          # binarka ONNX (format YOLO: [1,N,5+num_classes] output)
  labels.txt          # jedna klasa per wiersz, w tej samej kolejności co trenowano
```

Opcjonalnie można dodać:

```
  README.md           # opis modelu, źródło, datę treningu
  metadata.json       # input_size, mean/std, confidence_threshold
```

`ModelSeeder` przy starcie:
1. Skanuje `runtime/models/*/model.onnx`
2. Dla każdego znalezionego — sprawdza czy jest już w bazie (po absolutnej ścieżce)
3. Jeśli nie → tworzy nowy dokument `MLModel` z `Enabled=false` (user musi aktywować w UI `/models`)

## Dlaczego ONNX, nie .pt?

Aplikacja używa **ONNXRuntime** do inferencji — jest szybki (~2× szybszy niż PyTorch na CPU),
nie wymaga pytorcha w runtime (tylko ~40 MB dll-ek vs 2 GB pytorch+cuda), i działa identycznie
na Windows/Linux/macOS. Konwersja `.pt → .onnx` jest jednorazowa i zautomatyzowana.

## Rozmiary i wydajność (orientacyjne)

| Model | Rozmiar | CPU inference (1080p) | mAP50 COCO |
|-------|---------|----------------------|------------|
| yolov8n | 12 MB | ~25 ms | 37.3 |
| yolov8s | 44 MB | ~45 ms | 44.9 |
| yolov8m | 101 MB | ~90 ms | 50.2 |
| yolov8l | 167 MB | ~160 ms | 52.9 |
| yolov8x | 262 MB | ~260 ms | 53.9 |

Dla 1-5 kamer @ 5s interval — `yolov8n` wystarcza (~5% CPU).
Dla 10+ kamer albo krótszych interwałów — rozważ GPU lub wersję `s`.
