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
# Pobiera z HuggingFace gotowe ONNX — JEDYNA ścieżka (poprzedni ultralytics export
# produkował closed-set model z zamrożonym vocab COCO, co psuło text-prompt path).
#
# Źródła:
#   • Detection model: jquadrino/yolo-world-onnx — 2 inputs (image + text_features [N,classes,512]),
#     2 outputs (scores [N,8400,classes] sigmoid + boxes [N,8400,4] xyxy w 640×640).
#     ~400 MB FP32. Licencja MIT.
#   • CLIP text encoder + tokenizer: Xenova/clip-vit-base-patch32 — embed dim 512, MIT.
#
# Struktura runtime/models/yolo-world-v2-s/:
#   model.onnx             — image detection (2 inputs / 2 outputs split format)
#   text-encoder.onnx      — CLIP ViT-B/32 text encoder
#   tokenizer/vocab.json   — CLIP BPE vocab
#   tokenizer/merges.txt   — CLIP BPE merges
#   labels.txt             — seed labels (vendor opcjonalny — user podaje custom prompts)
#   README.md              — opis
export_yolo_world_v2s() {
    local target_dir="$MODELS_ROOT/yolo-world-v2-s"
    mkdir -p "$target_dir/tokenizer"

    if [ -f "$target_dir/model.onnx" ] && [ -f "$target_dir/text-encoder.onnx" ] \
        && [ -f "$target_dir/tokenizer/vocab.json" ] && [ -f "$target_dir/tokenizer/merges.txt" ]; then
        echo "  ✓ yolo-world-v2-s kompletny, pomijam"
        return 0
    fi

    hf_curl() {
        local dest="$1"; local url="$2"
        if [ -n "${HF_TOKEN:-}" ]; then
            curl -fL -H "Authorization: Bearer $HF_TOKEN" -o "$dest" "$url"
        else
            curl -fL -o "$dest" "$url"
        fi
    }

    [ -n "${HF_TOKEN:-}" ] && echo "  ℹ Używam HF_TOKEN z środowiska."

    local CLIP_BASE="https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main"
    local YW_BASE="https://huggingface.co/jquadrino/yolo-world-onnx/resolve/main"

    echo "  ↓ Pobieram YOLO-World detection ONNX (~400 MB) z jquadrino/yolo-world-onnx..."
    if ! hf_curl "$target_dir/model.onnx" "$YW_BASE/yolo-world.onnx"; then
        echo "  ✗ Pobieranie yolo-world.onnx nieudane. Sprawdź dostęp do huggingface.co."
        return 1
    fi
    echo "    ✓ model.onnx ($(du -h "$target_dir/model.onnx" | cut -f1))"

    echo "  ↓ Pobieram CLIP text encoder ONNX (~242 MB) z Xenova/clip-vit-base-patch32..."
    if ! hf_curl "$target_dir/text-encoder.onnx" "$CLIP_BASE/onnx/text_model.onnx"; then
        echo "  ✗ Nie udało się pobrać CLIP text-encoder."
        return 1
    fi
    echo "    ✓ text-encoder.onnx ($(du -h "$target_dir/text-encoder.onnx" | cut -f1))"

    echo "  ↓ Pobieram CLIP BPE tokenizer (vocab.json + merges.txt)..."
    hf_curl "$target_dir/tokenizer/vocab.json" "$CLIP_BASE/vocab.json" \
        || { echo "  ✗ Nie udało się pobrać vocab.json"; return 1; }
    hf_curl "$target_dir/tokenizer/merges.txt" "$CLIP_BASE/merges.txt" \
        || { echo "  ✗ Nie udało się pobrać merges.txt"; return 1; }

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
# YOLO-World v2 (open-vocabulary, dynamic 2-input)

**Source**: `jquadrino/yolo-world-onnx` (HuggingFace, MIT) + `Xenova/clip-vit-base-patch32` (CLIP text encoder).

**Architektura**:
- `model.onnx` — 2 inputs: `images [B,3,640,640]` + `text_features [B,classes,512]`
- 2 outputs: `scores [B,8400,classes]` (sigmoid probs) + `boxes [B,8400,4]` (xyxy w 640×640 input space)
- Open-vocabulary: vocabulary nie jest zamrożone w wagach — passujesz dowolne klasy jako embeddings z CLIP-a runtime
- Producer: pytorch 2.3.1, opset 12

**WAŻNE — historyczny bug fix (2026-04-27)**:
Wcześniejsza wersja tego folderu zawierała `model.onnx` z `ultralytics yolo export model=yolov8s-worldv2.pt`,
który produkował **closed-set** model z zamrożonym vocab COCO (1 input, brak text path). Custom prompts były
silently ignorowane przez sieć, a my relabel-owaliśmy detekcje przez `prompts[s.cls]` → user widział "person"
z labelem "dog". Naprawione przez przejście na jquadrino's dynamic export + defensive guard w
`OnnxYoloWorldDetector.DetectWithPromptsAsync` (rzuca clear error gdy isDynamic=false + custom prompts).

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
