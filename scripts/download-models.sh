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

# Aktywuj + sprawdź czy ultralytics + transformers są zainstalowane
# transformers jest potrzebne do eksportu CLIP text encoder w trybie pre-projection
# (zob. scripts/export-clip-text-encoder.py — Xenova ONNX nie pasuje do YOLO-World).
if ! "$VENV_DIR/bin/python" -c "import ultralytics, transformers, onnxscript" 2>/dev/null; then
    echo "→ Instaluję ultralytics + transformers + onnxscript (może potrwać kilka minut — ściąga pytorch)..."
    "$VENV_DIR/bin/pip" install --upgrade pip > /dev/null 2>&1
    "$VENV_DIR/bin/pip" install ultralytics onnx onnxruntime "transformers<5.0" onnxscript
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

# ── Specialty BHP model: Hugging Face .pt → ONNX export ───────────────────
# Pobiera fine-tuned YOLOv8 .pt z HuggingFace, eksportuje do ONNX, zachowuje class names
# z weights (m.names dict). Działa dla DOWOLNEGO yolov8 fine-tune (PPE, fire, weapon, ...).
# $1 = HF resolve URL do .pt
# $2 = target dir name (np. yolov8n-ppe)
# $3 = display name dla logów
export_hf_yolov8_pt() {
    local hf_url="$1"
    local target_dir="$2"
    local display="$3"
    local full_path="$MODELS_ROOT/$target_dir"
    mkdir -p "$full_path"

    if [ -f "$full_path/model.onnx" ] && [ -f "$full_path/labels.txt" ]; then
        echo "  ✓ $display już istnieje w $target_dir/, pomijam"
        return 0
    fi

    local tmp_pt="/tmp/sv-bhp-${target_dir}.pt"
    echo "  ↓ Pobieram $display .pt z $hf_url..."
    if ! curl -fL --progress-bar -o "$tmp_pt" "$hf_url" 2>&1 | tail -2; then
        echo "  ✗ Pobieranie .pt nieudane."
        return 1
    fi

    echo "  → Eksportuję do ONNX + ekstrahuję labels (z m.names dict)..."
    "$VENV_DIR/bin/python" - "$tmp_pt" "$full_path/model.onnx" "$full_path/labels.txt" <<'PY'
import sys, os
from ultralytics import YOLO
src, dst_onnx, dst_labels = sys.argv[1], sys.argv[2], sys.argv[3]
m = YOLO(src)
print(f"  Classes ({len(m.names)}): {list(m.names.values())}")
onnx_out = m.export(format="onnx", imgsz=640, opset=12, dynamic=True, verbose=False)
os.rename(onnx_out, dst_onnx)
with open(dst_labels, "w", encoding="utf-8") as f:
    for i in range(len(m.names)):
        f.write(m.names[i] + "\n")
print(f"  → {dst_onnx} ({os.path.getsize(dst_onnx)/1024/1024:.1f} MB)")
PY
    rm -f "$tmp_pt"
    [ -f "$full_path/model.onnx" ] && echo "  ✓ $display gotowy ($(du -h "$full_path/model.onnx" | cut -f1))" || return 1
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

# ── YOLO-World USUNIĘTY 2026-04-27 ─────────────────────────────────────────
# Model dawał false positives — prompty matchowały wizualnie podobne fragmenty
# (np. czerwone paski na ustach jako "pants") zamiast prawdziwych obiektów.
# Zastąpiony przez OWLv2 (Google, Apache 2.0). Dla CLIP text encoder + tokenizer
# (używane też przez YOLOE) zachowane export-clip-text-encoder.py i pliki w
# runtime/models/yolo-world-v2-s/ (tylko text-encoder.onnx + tokenizer/, bez model.onnx).

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

# ── OWLv2 (Google, Apache 2.0, ViT-based open-vocab) ──────────────────────
# Pre-built fused ONNX z onnx-community. Single model.onnx zawiera image encoder + text encoder
# + detection head. Lepsza detekcja rzadkich klas i części obiektów (face, pants, shoe) vs
# YOLO-World v2-s. ~614MB FP32, 153M params, input 960×960.
export_owlv2_base() {
    local target_dir="$MODELS_ROOT/owlv2-base"
    mkdir -p "$target_dir/tokenizer"

    if [ -f "$target_dir/model.onnx" ] \
        && [ -f "$target_dir/preprocessor_config.json" ] \
        && [ -f "$target_dir/tokenizer/vocab.json" ] \
        && [ -f "$target_dir/tokenizer/merges.txt" ]; then
        echo "  ✓ owlv2-base kompletny, pomijam"
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

    local OWLV2_BASE="https://huggingface.co/onnx-community/owlv2-base-patch16-ensemble-ONNX/resolve/main"
    local OWLV2_RAW="https://huggingface.co/onnx-community/owlv2-base-patch16-ensemble-ONNX/raw/main"

    echo "  ↓ Pobieram OWLv2 detection ONNX (~614 MB)..."
    if ! hf_curl "$target_dir/model.onnx" "$OWLV2_BASE/onnx/model.onnx"; then
        echo "  ✗ Pobieranie owlv2 model.onnx nieudane."
        return 1
    fi
    echo "    ✓ model.onnx ($(du -h "$target_dir/model.onnx" | cut -f1))"

    echo "  ↓ Pobieram preprocessor_config.json + config.json..."
    hf_curl "$target_dir/preprocessor_config.json" "$OWLV2_RAW/preprocessor_config.json" \
        || { echo "  ✗ preprocessor_config.json"; return 1; }
    hf_curl "$target_dir/config.json" "$OWLV2_RAW/config.json" \
        || { echo "  ✗ config.json"; return 1; }

    echo "  ↓ Pobieram CLIP BPE tokenizer (vocab.json + merges.txt)..."
    hf_curl "$target_dir/tokenizer/vocab.json" "$OWLV2_BASE/vocab.json" \
        || { echo "  ✗ vocab.json"; return 1; }
    hf_curl "$target_dir/tokenizer/merges.txt" "$OWLV2_BASE/merges.txt" \
        || { echo "  ✗ merges.txt"; return 1; }

    cat > "$target_dir/labels.txt" <<'EOF'
person
face
hand
shoe
pants
shirt
fire
smoke
helmet
car
dog
cat
EOF

    cat > "$target_dir/README.md" <<'EOF'
# OWLv2 base patch16 ensemble (open-vocabulary)

**Source**: `onnx-community/owlv2-base-patch16-ensemble-ONNX` (Google, Apache 2.0)

**Architektura**: Vision Transformer (patch16, 60×60 = 3600 anchors) + CLIP-style text encoder.
Single fused ONNX (~614 MB FP32, 153M params), 3 inputs (pixel_values, input_ids, attention_mask),
4 outputs (logits + pred_boxes + 2 ignorowane).

**Vs YOLO-World v2-s**: lepsza detekcja rzadkich klas i części obiektów (face, hand, pants, shoe).
Wolniejszy ~3-5× CPU (960² + ViT vs 640² + YOLO conv). Trenowany na DRAGON + Visual Genome.

Pipeline SafeView automatycznie rozpoznaje ten folder jako `DetectorBackend.OwlV2`
(po obecności `preprocessor_config.json` + `tokenizer/`) i ustawia `Capabilities = TextPrompts`,
`InputSize = 960`.
EOF

    echo "  ✓ owlv2-base gotowy"
}

# ── OWLv2 large patch14 ensemble (Google, Apache 2.0) ──────────────────────
# Większy variant OWLv2 — ~430M params (vs 153M w base), znacznie szersze pokrycie
# rzadkich klas, najlepsze zero-shot detection mAP per Google paper. Wolniejszy
# 3-4× vs base (CPU). Pobiera ~1.74 GB FP32. Polecane do produkcji jakościowej;
# base zostaje dla szybkich testów / dev-loop.
export_owlv2_large() {
    local target_dir="$MODELS_ROOT/owlv2-large"
    mkdir -p "$target_dir/tokenizer"

    if [ -f "$target_dir/model.onnx" ] \
        && [ -f "$target_dir/preprocessor_config.json" ] \
        && [ -f "$target_dir/tokenizer/vocab.json" ] \
        && [ -f "$target_dir/tokenizer/merges.txt" ]; then
        echo "  ✓ owlv2-large kompletny, pomijam"
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

    local OWLV2_BASE="https://huggingface.co/onnx-community/owlv2-large-patch14-ensemble-ONNX/resolve/main"
    local OWLV2_RAW="https://huggingface.co/onnx-community/owlv2-large-patch14-ensemble-ONNX/raw/main"

    echo "  ↓ Pobieram OWLv2 large patch14 ensemble ONNX (~1.74 GB)..."
    echo "    To może chwilę potrwać — duży model dla najlepszej jakości detekcji rzadkich klas."
    if ! hf_curl "$target_dir/model.onnx" "$OWLV2_BASE/onnx/model.onnx"; then
        echo "  ✗ Pobieranie owlv2-large model.onnx nieudane."
        return 1
    fi
    echo "    ✓ model.onnx ($(du -h "$target_dir/model.onnx" | cut -f1))"

    echo "  ↓ Pobieram preprocessor_config.json + config.json..."
    hf_curl "$target_dir/preprocessor_config.json" "$OWLV2_RAW/preprocessor_config.json" \
        || { echo "  ✗ preprocessor_config.json"; return 1; }
    hf_curl "$target_dir/config.json" "$OWLV2_RAW/config.json" \
        || { echo "  ✗ config.json"; return 1; }

    echo "  ↓ Pobieram CLIP BPE tokenizer (vocab.json + merges.txt)..."
    hf_curl "$target_dir/tokenizer/vocab.json" "$OWLV2_BASE/vocab.json" \
        || { echo "  ✗ vocab.json"; return 1; }
    hf_curl "$target_dir/tokenizer/merges.txt" "$OWLV2_BASE/merges.txt" \
        || { echo "  ✗ merges.txt"; return 1; }

    cat > "$target_dir/labels.txt" <<'EOF'
person
face
hand
shoe
pants
shirt
fire
smoke
helmet
car
dog
cat
EOF

    cat > "$target_dir/README.md" <<'EOF'
# OWLv2 large patch14 ensemble (open-vocabulary)

**Source**: `onnx-community/owlv2-large-patch14-ensemble-ONNX` (Google, Apache 2.0)

**Architektura**: ViT-L/patch14 backbone (~430M params, ~3× większy niż base).
Single fused ONNX (~1.74 GB FP32). Te same 3 inputs / 4 outputs co base — pipeline
SafeView nie wymaga zmian, ten sam `OnnxOwlV2Detector`.

**Vs owlv2-base**: znacznie szersze pokrycie rzadkich klas (najlepsze zero-shot
mAP per Google paper). Wolniejszy ~3-4× CPU. Polecane do produkcji jakościowej;
base zostaje dla szybkich testów / dev-loop.

Pipeline SafeView automatycznie rozpoznaje ten folder jako `DetectorBackend.OwlV2`
(po obecności `preprocessor_config.json` + `tokenizer/`).
EOF

    echo "  ✓ owlv2-large gotowy"
}

# ── Main ────────────────────────────────────────────────────────────────────
# Default base — out-of-box dla BHP / industrial safety: COCO general + PPE + fire/smoke + OWLv2 open-vocab
DEFAULT_TARGETS=(yolov8n-coco yolov8s-coco yolov8n-ppe yolov8s-fire-smoke owlv2-base)
TARGETS=("${@:-${DEFAULT_TARGETS[@]}}")
if [ "$#" -eq 0 ]; then
    TARGETS=("${DEFAULT_TARGETS[@]}")
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
        # ── BHP specialty fine-tunes ──
        yolov8n-ppe)        export_hf_yolov8_pt \
                                "https://huggingface.co/Hansung-Cho/yolov8-ppe-detection/resolve/main/best.pt" \
                                "yolov8n-ppe" \
                                "PPE detection (Hansung-Cho, MIT, hardhat/mask/vest/person/cone/machinery/vehicle)" ;;
        yolov8s-fire-smoke) export_hf_yolov8_pt \
                                "https://huggingface.co/leeyunjai/yolo11-firedetect/resolve/main/firedetect-11s.pt" \
                                "yolov8s-fire-smoke" \
                                "Fire/Smoke detection (leeyunjai, YOLO11s, 2 klasy: fire/smoke)" ;;
        # ── Open-vocab (text prompts) ──
        owlv2-base)         export_owlv2_base ;;
        owlv2-large)        export_owlv2_large ;;
        # ── Visual prompts (AGPL — opt-in) ──
        yoloe-11s)          export_yoloe_11s ;;
        *)
            echo "  ✗ Nieznany model: $target"
            echo "     Dostępne:"
            echo "       COCO general:      yolov8n-coco, yolov8s-coco, yolov8m-coco, yolov8l-coco"
            echo "       BHP specialty:     yolov8n-ppe, yolov8s-fire-smoke"
            echo "       Open-vocab:        owlv2-base, owlv2-large"
            echo "       Visual prompts:    yoloe-11s (AGPL)"
            echo "     YOLO-World v2 USUNIĘTY 2026-04-27 — używaj OWLv2."
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
