#!/usr/bin/env python3
"""
Eksport CLIP ViT-B/32 text encoder do ONNX zwracającego pooler_output (PRE-projection).

Dlaczego custom export zamiast pobrania z HF:
- Xenova/clip-vit-base-patch32/onnx/text_model.onnx zwraca POST-projection embeddings
  (text_projection @ pooler_output)
- YOLO-World v2 oczekuje PRE-projection (pooler_output) — ma własną text_projection
  layer fused w wagach modelu
- Karmienie YOLO-World post-projection = duplikowane projection = garbage scores
  (np. "pants" → 0.0009 zamiast 0.43)

Bug znaleziony 2026-04-27: detekcja YOLO-World działała tylko dla "person" bo embedding
osoby jest tak rozpoznawalny że nawet po podwójnym projection model coś detektował.
Pozostałe klasy (pants, tshirt, ...) → score ~0.001 → 0 detekcji.

Wymaga: torch, transformers, onnx (już w runtime/models/.venv/).
"""
import os
import sys
import torch
from transformers import CLIPModel, CLIPTokenizer

OUT_PATH = sys.argv[1] if len(sys.argv) > 1 else "runtime/models/yolo-world-v2-s/text-encoder.onnx"
MODEL_NAME = "openai/clip-vit-base-patch32"


class TextEncoderPooler(torch.nn.Module):
    """Wraps CLIP text_model żeby return pooler_output (NIE projected text_features)."""
    def __init__(self, text_model):
        super().__init__()
        self.text_model = text_model

    def forward(self, input_ids):
        return self.text_model(input_ids=input_ids).pooler_output  # [B, 512]


def main():
    print(f"[export-clip] Loading {MODEL_NAME}...")
    full = CLIPModel.from_pretrained(MODEL_NAME).eval()
    tok = CLIPTokenizer.from_pretrained(MODEL_NAME)

    wrapper = TextEncoderPooler(full.text_model).eval()
    dummy = tok(["test"], padding="max_length", max_length=77, truncation=True,
                return_tensors="pt")["input_ids"]

    os.makedirs(os.path.dirname(OUT_PATH) or ".", exist_ok=True)
    print(f"[export-clip] Exporting to {OUT_PATH}...")
    torch.onnx.export(
        wrapper, (dummy,), OUT_PATH,
        input_names=["input_ids"],
        output_names=["pooler_output"],
        dynamic_axes={"input_ids": {0: "batch_size"},
                      "pooler_output": {0: "batch_size"}},
        opset_version=14,
        dynamo=False,  # legacy exporter — stable for transformer architectures
    )
    size_mb = os.path.getsize(OUT_PATH) / 1024 / 1024
    print(f"[export-clip] OK: {OUT_PATH} ({size_mb:.1f} MB)")

    # Sanity check: verify ONNX matches PyTorch within numerical tolerance
    try:
        import onnxruntime as ort
        import numpy as np
        sess = ort.InferenceSession(OUT_PATH, providers=["CPUExecutionProvider"])
        out_onnx = sess.run(None, {"input_ids": dummy.numpy().astype(np.int64)})[0]
        with torch.no_grad():
            out_torch = wrapper(dummy).numpy()
        cos = float(np.dot(
            out_onnx[0] / np.linalg.norm(out_onnx[0]),
            out_torch[0] / np.linalg.norm(out_torch[0]),
        ))
        print(f"[export-clip] Sanity: cos(ONNX, PyTorch) = {cos:.6f}")
        if cos < 0.999:
            print(f"[export-clip] WARNING: cos < 0.999, export może być wadliwy")
            sys.exit(1)
    except ImportError:
        print("[export-clip] (skip sanity check — onnxruntime not installed)")


if __name__ == "__main__":
    main()
