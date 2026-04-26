#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Pobiera pretrained YOLO modele COCO i konwertuje do ONNX, do runtime/models/.
#
# Wymagania:
#  • Python 3.9+ w PATH
#  • curl w PATH
#  • Internet (~500 MB pip + ~250 MB modele w tym YOLO-World)
#  • Miejsce na dysku: ~1.5 GB dla venv + modeli
#
# Modele pobierane automatycznie (domyślnie):
#  • yolov8n-coco      — COCO 80 klas, ~12 MB (classical closed-set, MVP)
#  • yolov8s-coco      — COCO 80 klas, ~44 MB (classical, dokładniejszy)
#  • yolo-world-v2-s   — open-vocab Apache 2.0, ~30 MB ONNX + ~150 MB CLIP text encoder
#
# Dodatkowe (manual):
#  • yolov8m-coco, yolov8l-coco — classical (argument linii komend)
#
# Użycie:
#   ./scripts/download-models.sh                    # pobiera wszystkie default
#   ./scripts/download-models.sh yolov8n-coco       # tylko classical nano
#   ./scripts/download-models.sh yolo-world-v2-s    # tylko open-vocab
# ─────────────────────────────────────────────────────────────────────────────

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODELS_ROOT="$REPO_ROOT/runtime/models"
VENV_DIR="$MODELS_ROOT/.venv"

# ── Python venv + ultralytics ──────────────────────────────────────────────
if [ ! -d "$VENV_DIR" ]; then
    echo "→ Tworzę Python venv w $VENV_DIR"
    python3 -m venv "$VENV_DIR"
fi

# Aktywuj + sprawdź czy ultralytics jest zainstalowany
if ! "$VENV_DIR/bin/python" -c "import ultralytics" 2>/dev/null; then
    echo "→ Instaluję ultralytics (może potrwać kilka minut — ściąga pytorch)..."
    "$VENV_DIR/bin/pip" install --upgrade pip > /dev/null 2>&1
    "$VENV_DIR/bin/pip" install ultralytics onnx onnxruntime
fi

echo "→ ultralytics gotowy: $("$VENV_DIR/bin/python" -c "import ultralytics; print(ultralytics.__version__)")"

# ── Etykiety COCO 80 (źródło: oryginalny zestaw) ───────────────────────────
# Funkcja zapisuje labels.txt dla COCO-80
write_coco_labels() {
    local target="$1"
    cat > "$target" <<'EOF'
person
bicycle
car
motorcycle
airplane
bus
train
truck
boat
traffic light
fire hydrant
stop sign
parking meter
bench
bird
cat
dog
horse
sheep
cow
elephant
bear
zebra
giraffe
backpack
umbrella
handbag
tie
suitcase
frisbee
skis
snowboard
sports ball
kite
baseball bat
baseball glove
skateboard
surfboard
tennis racket
bottle
wine glass
cup
fork
knife
spoon
bowl
banana
apple
sandwich
orange
broccoli
carrot
hot dog
pizza
donut
cake
chair
couch
potted plant
bed
dining table
toilet
tv
laptop
mouse
remote
keyboard
cell phone
microwave
oven
toaster
sink
refrigerator
book
clock
vase
scissors
teddy bear
hair drier
toothbrush
EOF
}

# ── Export model function ──────────────────────────────────────────────────
# $1 = model name (yolov8n, yolov8s, ...)
# $2 = target dir (yolov8n-coco)
export_coco_model() {
    local name="$1"
    local target_dir="$2"
    local full_path="$MODELS_ROOT/$target_dir"
    mkdir -p "$full_path"

    if [ -f "$full_path/model.onnx" ]; then
        echo "  ✓ $name.onnx już istnieje w $target_dir/, pomijam"
        return 0
    fi

    echo "  ↓ Pobieram $name.pt i konwertuję do ONNX (dynamic batch dla SAHI batching)..."
    (
        cd "$full_path"
        # dynamic=True → input shape [batch,3,H,W] z dynamiczną pierwszą osią.
        # Pozwala SlicedDetector wysłać N tiles w jednym inference call (Faza 4 batching).
        # Bez dynamic wygeneruje się batch=1, SlicedDetector wykryje to i zrobi fallback sekwencyjny.
        "$VENV_DIR/bin/yolo" export "model=$name.pt" format=onnx imgsz=640 opset=12 dynamic=True 2>&1 | tail -5
    )

    if [ -f "$full_path/$name.onnx" ]; then
        mv "$full_path/$name.onnx" "$full_path/model.onnx"
        # usuwamy pozostałości .pt po konwersji (~12-44 MB)
        rm -f "$full_path/$name.pt"
        write_coco_labels "$full_path/labels.txt"
        echo "  ✓ $target_dir/model.onnx ($(du -h "$full_path/model.onnx" | cut -f1))"
    else
        echo "  ✗ Błąd eksportu — sprawdź output ultralytics"
        return 1
    fi
}

# ── YOLO-World v2 open-vocab (Apache 2.0, Tencent) ─────────────────────────
# Pobiera z HuggingFace gotowe ONNX (nie wymaga eksportu przez Python/torch).
# Repo: onnx-community/YOLO-World-v2-s (autor: community, Apache 2.0 od Tencent)
# Alternatywa: Xenova/yolo-world (też ONNX, browser-oriented).
#
# Struktura runtime/models/yolo-world-v2-s/:
#   model.onnx             — image detection ONNX (open-vocab, 2 inputs: image + text_embeddings)
#   text-encoder.onnx      — CLIP ViT-B/32 text encoder
#   tokenizer/vocab.json   — CLIP BPE vocab (49408 tokens)
#   tokenizer/merges.txt   — CLIP BPE merges
#   labels.txt             — default lista klas (seed — user może użyć dowolnych promptów w TriggerDialog)
#   README.md              — opis
export_yolo_world_v2s() {
    local target_dir="$MODELS_ROOT/yolo-world-v2-s"
    mkdir -p "$target_dir/tokenizer"

    if [ -f "$target_dir/model.onnx" ] && [ -f "$target_dir/text-encoder.onnx" ] \
        && [ -f "$target_dir/tokenizer/vocab.json" ] && [ -f "$target_dir/tokenizer/merges.txt" ]; then
        echo "  ✓ yolo-world-v2-s kompletny, pomijam"
        return 0
    fi

    # HF_TOKEN (opcjonalny) — dla repos gated. Używamy funkcji-wrappera zamiast tablicy,
    # żeby uniknąć problemu z `set -u` na pustej tablicy `${AUTH[@]}`.
    hf_curl() {
        local dest="$1"; local url="$2"
        if [ -n "${HF_TOKEN:-}" ]; then
            curl -fL -H "Authorization: Bearer $HF_TOKEN" -o "$dest" "$url"
        else
            curl -fL -o "$dest" "$url"
        fi
    }

    if [ -n "${HF_TOKEN:-}" ]; then
        echo "  ℹ Używam HF_TOKEN z środowiska."
    fi

    local CLIP_BASE="https://huggingface.co/openai/clip-vit-base-patch32/resolve/main"

    # Primary ścieżka: ultralytics Python venv pobiera oficjalne wagi + eksportuje do ONNX.
    # Ten sam flow co dla YOLOv8 classical (już sprawdzony). Nie wymaga HuggingFace ani token.
    # Ultralytics samo pobiera z własnego CDN (github releases) — stabilne.
    echo "  ↓ Eksportuję YOLO-World v2-s przez ultralytics (pobiera wagi + konwertuje ONNX)..."
    (
        cd "$target_dir"
        # yolov8s-worldv2 = YOLO-World v2 small. Ultralytics od v8.1+ ma to natywnie.
        "$VENV_DIR/bin/yolo" export "model=yolov8s-worldv2.pt" format=onnx imgsz=640 opset=12 dynamic=True 2>&1 | tail -8
    )

    # Ultralytics zapisuje plik o takiej samej nazwie co model (yolov8s-worldv2.onnx).
    # Zmieniamy nazwę na konsystentne model.onnx + usuwamy .pt żeby oszczędzić dysk.
    if [ -f "$target_dir/yolov8s-worldv2.onnx" ]; then
        mv "$target_dir/yolov8s-worldv2.onnx" "$target_dir/model.onnx"
        rm -f "$target_dir/yolov8s-worldv2.pt"
        echo "    ✓ model.onnx z ultralytics ($(du -h "$target_dir/model.onnx" | cut -f1))"
    else
        echo "  ✗ ultralytics export nie wyprodukował pliku ONNX."
        echo "     Sprawdź czy ultralytics jest zainstalowany w wersji >= 8.1 i czy ma dostęp do netu."
        echo "     Alternatywnie: pobierz yolov8s-worldv2 ręcznie i umieść jako $target_dir/model.onnx"
        return 1
    fi

    echo "  ↓ Pobieram CLIP text encoder ONNX (~150 MB)..."
    if hf_curl "$target_dir/text-encoder.onnx" "$CLIP_BASE/onnx/text_model.onnx" 2>/dev/null; then
        echo "    ✓ text-encoder z openai/clip-vit-base-patch32"
    else
        echo "  ✗ Nie udało się pobrać CLIP text-encoder. Sprawdź dostęp do huggingface.co."
        return 1
    fi

    echo "  ↓ Pobieram CLIP BPE tokenizer (vocab.json + merges.txt)..."
    hf_curl "$target_dir/tokenizer/vocab.json" "$CLIP_BASE/vocab.json" \
        || { echo "  ✗ Nie udało się pobrać vocab.json"; return 1; }
    hf_curl "$target_dir/tokenizer/merges.txt" "$CLIP_BASE/merges.txt" \
        || { echo "  ✗ Nie udało się pobrać merges.txt"; return 1; }

    # Default seed labels — dowolna lista, user tak naprawdę użyje prompts z DetectionClass.
    # Wpis ma tylko pomóc ModelSeeder-owi zainicjować model z sensowną listą COCO-like.
    cat > "$target_dir/labels.txt" <<'EOF'
person
car
truck
bicycle
motorcycle
hard hat
safety vest
fire
smoke
EOF

    cat > "$target_dir/README.md" <<'EOF'
# YOLO-World v2-s (open-vocabulary)

Licencja: Apache 2.0 (Tencent AI Lab). Open-vocabulary detektor — klasy podawane są
jako prompty tekstowe przy inferencji (nie są zamrożone w wagach).

Bundled files:
- `model.onnx` — detection model, 2 inputs (image + text_embeddings)
- `text-encoder.onnx` — CLIP ViT-B/32 text encoder
- `tokenizer/` — CLIP BPE vocab + merges
- `labels.txt` — seed labels (nie jest używany przy dynamic prompt — user podaje swoje)

Pipeline SafeView automatycznie rozpoznaje ten folder jako `DetectorBackend.YoloWorld`
i ustawia `Capabilities = ClosedSet | TextPrompts`.
EOF

    echo "  ✓ yolo-world-v2-s gotowy"
}

# ── YOLOE (Ultralytics, text + visual prompts) — AGPL license ──────────────
# UWAGA LICENCJA: YOLOE jest dystrybuowany przez Ultralytics pod AGPL-3.0.
# Użycie w produkcji komercyjnej bez Enterprise License = otwarcie kodu całej aplikacji
# na AGPL (network copyleft). Dla SafeView (enterprise BHP produkt) zalecane:
#   - nie pobieraj YOLOE bez decyzji biznesowej
#   - architektura `IVisualPromptDetector` swap-ready → zamień na OWLv2 (Apache 2.0) w przyszłości
# Ten target zostawiony na wypadek gdy user ma Enterprise License albo developuje lokalnie.
#
# Struktura runtime/models/yoloe-11s/:
#   model.onnx           — detection head (image + text/visual embeddings → bboxes)
#   text-encoder.onnx    — CLIP text encoder (reuse z YOLO-World)
#   image-encoder.onnx   — ViT-image encoder dla visual prompts
#   tokenizer/vocab.json + merges.txt
#   labels.txt + README.md
export_yoloe_11s() {
    local target_dir="$MODELS_ROOT/yoloe-11s"
    mkdir -p "$target_dir/tokenizer"

    echo ""
    echo "⚠ UWAGA — YOLOE jest pod licencją AGPL-3.0 (Ultralytics)."
    echo "  Dla wdrożeń komercyjnych zalecana Enterprise License od Ultralytics."
    echo "  Alternatywa permissive: OWLv2 (Apache 2.0) — swap w przyszłości."
    echo ""
    read -p "  Kontynuować pobieranie YOLOE? [y/N]: " confirm
    case "$confirm" in
        y|Y|yes|YES) ;;
        *) echo "  ✗ Pomijam YOLOE."; return 0 ;;
    esac

    if [ -f "$target_dir/model.onnx" ] && [ -f "$target_dir/text-encoder.onnx" ] \
        && [ -f "$target_dir/image-encoder.onnx" ]; then
        echo "  ✓ yoloe-11s kompletny, pomijam"
        return 0
    fi

    # Aktualna lokalizacja YOLOE ONNX — HuggingFace Ultralytics. Pattern URL może się
    # zmienić między wersjami; gdy się zepsuje, update skrypt + referencję w README.
    local HF_BASE="https://huggingface.co/Ultralytics/YOLOE/resolve/main"
    local CLIP_BASE="https://huggingface.co/openai/clip-vit-base-patch32/resolve/main"

    echo "  ↓ Pobieram YOLOE detection model (~40 MB)..."
    curl -fL -o "$target_dir/model.onnx" "$HF_BASE/yoloe-11s-seg.onnx" 2>/dev/null \
        || curl -fL -o "$target_dir/model.onnx" "$HF_BASE/yoloe-11s.onnx" \
        || { echo "  ✗ Nie udało się pobrać YOLOE model.onnx — sprawdź dostępność na HuggingFace"; return 1; }

    echo "  ↓ Pobieram CLIP text encoder (~150 MB)..."
    curl -fL -o "$target_dir/text-encoder.onnx" "$CLIP_BASE/onnx/text_model.onnx" \
        || { echo "  ✗ Nie udało się pobrać text-encoder"; return 1; }

    echo "  ↓ Pobieram CLIP image encoder (~150 MB)..."
    curl -fL -o "$target_dir/image-encoder.onnx" "$CLIP_BASE/onnx/vision_model.onnx" \
        || { echo "  ✗ Nie udało się pobrać image-encoder"; return 1; }

    echo "  ↓ Pobieram tokenizer..."
    curl -fL -o "$target_dir/tokenizer/vocab.json" "$CLIP_BASE/vocab.json" \
        || { echo "  ✗ Nie udało się pobrać vocab.json"; return 1; }
    curl -fL -o "$target_dir/tokenizer/merges.txt" "$CLIP_BASE/merges.txt" \
        || { echo "  ✗ Nie udało się pobrać merges.txt"; return 1; }

    cat > "$target_dir/labels.txt" <<'EOF'
person
car
truck
fire
smoke
EOF

    cat > "$target_dir/README.md" <<'EOF'
# YOLOE-11s (text + visual prompts)

**LICENCJA: AGPL-3.0** (Ultralytics). Dla wdrożeń komercyjnych wymagana
Enterprise License od Ultralytics, lub zamiana na OWLv2 (Apache 2.0).

Bundled files:
- `model.onnx` — detection head
- `text-encoder.onnx` — CLIP text encoder (dla text-prompts)
- `image-encoder.onnx` — ViT image encoder (dla visual-prompts)
- `tokenizer/` — CLIP BPE vocab + merges

Pipeline SafeView automatycznie rozpoznaje ten folder jako `DetectorBackend.YoloE`
i ustawia `Capabilities = ClosedSet | TextPrompts | VisualPrompts`.

Swap na OWLv2 w przyszłości: zmień DetectorBackend enum, impl `OnnxOwlV2Detector`,
rejestracja w DI. Zero zmian w Domain, Trigger, UI.
EOF

    echo "  ✓ yoloe-11s gotowy"
}

# ── Main ────────────────────────────────────────────────────────────────────
TARGETS=("${@:-yolov8n-coco yolov8s-coco yolo-world-v2-s}")
if [ "$#" -eq 0 ]; then
    TARGETS=(yolov8n-coco yolov8s-coco yolo-world-v2-s)
fi

for target in "${TARGETS[@]}"; do
    echo ""
    echo "════════════════════════════════════════════"
    echo "  Model: $target"
    echo "════════════════════════════════════════════"
    case "$target" in
        yolov8n-coco)       export_coco_model "yolov8n" "yolov8n-coco" ;;
        yolov8s-coco)       export_coco_model "yolov8s" "yolov8s-coco" ;;
        yolov8m-coco)       export_coco_model "yolov8m" "yolov8m-coco" ;;
        yolov8l-coco)       export_coco_model "yolov8l" "yolov8l-coco" ;;
        yolo-world-v2-s)    export_yolo_world_v2s ;;
        yoloe-11s)          export_yoloe_11s ;;
        *)
            echo "  ✗ Nieznany model: $target"
            echo "     Dostępne: yolov8n-coco, yolov8s-coco, yolov8m-coco, yolov8l-coco,"
            echo "              yolo-world-v2-s, yoloe-11s"
            echo "     Dla fire/smoke + PPE — patrz runtime/models/README.md"
            continue
            ;;
    esac
done

echo ""
echo "════════════════════════════════════════════"
echo "  Gotowe. Zawartość $MODELS_ROOT:"
echo "════════════════════════════════════════════"
for d in "$MODELS_ROOT"/*/; do
    [ -d "$d" ] || continue
    [ "${d##*/.}" = "venv/" ] && continue
    name="$(basename "$d")"
    if [ -f "$d/model.onnx" ]; then
        echo "  ✓ $name — $(du -h "$d/model.onnx" | cut -f1)"
    else
        echo "  ○ $name — (pusty)"
    fi
done

echo ""
echo "Przy następnym uruchomieniu SafeView modele zostaną automatycznie"
echo "zarejestrowane w bazie (ModelSeeder). Potem aktywuj je w UI → Modele."
