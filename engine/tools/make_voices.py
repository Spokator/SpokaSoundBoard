"""Precomputes the target-voice « imprint » used by the voice engine, exactly as Seed-VC's real-time inference does
for its reference audio (first 3 s): style vector (CAMPPlus), prompt mel and prompt condition.

File format (little endian): b"SVCV", int32 version=1, int32 frames, then float32 style[192], mel[80 * frames]
(row-major, mel bin first), prompt[frames * 384] (row-major, frame first).
"""
import importlib.machinery
import os
import struct
import sys
import types

import librosa
import numpy as np
import torch
import torchaudio

ROOT = os.path.dirname(os.path.abspath(__file__))
os.chdir(ROOT)
sys.path.insert(0, ROOT)
gui = importlib.machinery.SourceFileLoader("rtgui", os.path.join(ROOT, "real-time-gui.py")).load_module()
gui.device = torch.device("cpu")
model, semantic_fn, vocoder, campplus, to_mel, mel_args = gui.load_models(
    types.SimpleNamespace(checkpoint_path=None, config_path=None, fp16=False, gpu=0))
from transformers import Wav2Vec2Model  # noqa: E402

# Same content encoder as content.onnx (fp32, eager attention), so the prompt matches what the engine computes.
xlsr = Wav2Vec2Model.from_pretrained("facebook/wav2vec2-xls-r-300m", attn_implementation="eager")
xlsr.encoder.layers = xlsr.encoder.layers[:12]
xlsr.eval()


def content(w16):
    x = (w16 - w16.mean(dim=-1, keepdim=True)) / torch.sqrt(w16.var(dim=-1, keepdim=True, unbiased=False) + 1e-7)
    return xlsr(x).last_hidden_state


src_dir, out_dir = sys.argv[1], sys.argv[2]
os.makedirs(out_dir, exist_ok=True)
sr = mel_args["sampling_rate"]
for f in sorted(os.listdir(src_dir)):
    if not f.endswith(".wav"):
        continue
    ref, _ = librosa.load(os.path.join(src_dir, f), sr=sr)
    ref = ref[:int(sr * 3.0)]
    with torch.no_grad():
        t = torch.from_numpy(ref)
        w16 = torchaudio.functional.resample(t, sr, 16000)
        s_ori = content(w16.unsqueeze(0))
        fbank = torchaudio.compliance.kaldi.fbank(w16.unsqueeze(0), num_mel_bins=80, dither=0, sample_frequency=16000)
        fbank = fbank - fbank.mean(dim=0, keepdim=True)
        style = campplus(fbank.unsqueeze(0))
        mel = to_mel(t.unsqueeze(0))
        prompt = model.length_regulator(s_ori, ylens=torch.LongTensor([mel.size(2)]), n_quantizers=3, f0=None)[0]
    frames = mel.size(2)
    assert prompt.shape == (1, frames, 384), prompt.shape
    path = os.path.join(out_dir, os.path.splitext(f)[0] + ".voice")
    with open(path, "wb") as fh:
        fh.write(b"SVCV" + struct.pack("<ii", 1, frames))
        fh.write(style.squeeze(0).numpy().astype("<f4").tobytes())
        fh.write(mel.squeeze(0).numpy().astype("<f4").tobytes())
        fh.write(prompt.squeeze(0).numpy().astype("<f4").tobytes())
    print(f, frames, os.path.getsize(path), flush=True)
