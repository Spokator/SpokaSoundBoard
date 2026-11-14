"""Exports what turns a recording into a voice for SpokaVoiceEngine --imprint (« Mes voix »), the voicecreator1 pack:
  campplus.onnx  Kaldi fbank [1, 300, 80] (3 s, mean-normalized) -> style [1, 192], CAMPPlus with fixed shapes, for the CPU
  fbank.bin      float32 [80, 257]: the Kaldi mel filter banks (16 kHz, 512-point FFT); the engine computes the fbank in C#
  melbasis.bin   float32 [80, 513]: librosa mel filter bank of the prompt mel spectrogram (22.05 kHz, n_fft 1024)
and checks them against PyTorch / librosa. Run from a Seed-VC checkout: python export_creator.py <output folder>"""
import importlib.machinery
import os
import sys
import types

import numpy as np
import onnxruntime as ort
import torch
import torch.nn as nn
import torchaudio

ROOT = os.path.dirname(os.path.abspath(__file__))
os.chdir(ROOT)
sys.path.insert(0, ROOT)
OUT = sys.argv[1]
gui = importlib.machinery.SourceFileLoader("rtgui", os.path.join(ROOT, "real-time-gui.py")).load_module()
gui.device = torch.device("cpu")
model, semantic_fn, vocoder, campplus, to_mel, mel_args = gui.load_models(
    types.SimpleNamespace(checkpoint_path=None, config_path=None, fp16=False, gpu=0))


import torchaudio.compliance.kaldi as kaldi  # noqa: E402


class KaldiFbank(nn.Module):
    """torchaudio.compliance.kaldi.fbank (80 bins, 16 kHz, dither 0, defaults otherwise) with the FFT written as a
    matrix product, which the ONNX exporter supports."""

    def __init__(self):
        super().__init__()
        n_fft = 512  # 400-sample frames rounded to a power of two
        k = torch.arange(n_fft // 2 + 1, dtype=torch.float64)
        n = torch.arange(n_fft, dtype=torch.float64)
        ang = 2 * torch.pi * n[:, None] * k[None, :] / n_fft
        self.register_buffer("cos", torch.cos(ang).float())
        self.register_buffer("sin", torch.sin(ang).float())
        banks, _ = kaldi.get_mel_banks(80, n_fft, 16000.0, 20.0, 0.0, 100.0, -500.0, 1.0)
        banks = torch.nn.functional.pad(banks, (0, 1), mode="constant", value=0)  # like kaldi.fbank
        self.register_buffer("banks", banks.float())

    def forward(self, wav16k):
        waveform = wav16k
        strided, _ = kaldi._get_window(waveform.squeeze(0), padded_window_size=512, window_size=400, window_shift=160,
                                       window_type=kaldi.POVEY, blackman_coeff=0.42, snip_edges=True, raw_energy=True,
                                       energy_floor=1.0, dither=0.0, remove_dc_offset=True, preemphasis_coefficient=0.97)
        re = strided @ self.cos
        im = strided @ self.sin
        power = re * re + im * im
        mel = power @ self.banks.T
        return torch.log(torch.clamp(mel, min=torch.finfo(torch.float32).eps))


from modules.campplus.layers import DenseLayer  # noqa: E402


def _dense_forward(self, x):
    # Same as DenseLayer.forward, with [..., 0] instead of squeeze(-1), which the exporter turns into an If node.
    if x.dim() == 2:
        return self.nonlinear(self.linear(x.unsqueeze(-1)))[:, :, 0]  # batch norm on [B, C, 1]: same result
    return self.nonlinear(self.linear(x))


DenseLayer.forward = _dense_forward

from modules.campplus.layers import CAMLayer  # noqa: E402


def _seg_pooling(self, x, seg_len=100, stype="avg"):
    # avg_pool1d(ceil_mode=True) averages the last, shorter segment over its own length; ONNX's AveragePool divides
    # it by the full kernel. Same result as PyTorch, written with explicit means (lengths are fixed at export).
    n = x.shape[-1]
    parts = []
    for start in range(0, n, seg_len):
        piece = x[..., start:min(start + seg_len, n)]
        m = piece.mean(dim=-1, keepdim=True) if stype == "avg" else piece.amax(dim=-1, keepdim=True)
        parts.append(m.expand(*piece.shape))
    return torch.cat(parts, dim=-1)


CAMLayer.seg_pooling = _seg_pooling


class Style(nn.Module):
    def __init__(self, c):
        super().__init__()
        self.c = c
        self.fbank = KaldiFbank()

    def forward(self, wav16k):
        fbank = self.fbank(wav16k)
        fbank = fbank - fbank.mean(dim=0, keepdim=True)
        return self.c(fbank.unsqueeze(0))


_w = torch.randn(1, 16000 * 2) * 0.1
_ref = kaldi.fbank(_w, num_mel_bins=80, dither=0, sample_frequency=16000)
_got = KaldiFbank()(_w)
print(f"fbank rewrite vs torchaudio: max abs diff {float((_ref - _got).abs().max()):.2e}", flush=True)


# CAMPPlus alone, on 3 s of fbank (298 frames, fixed: its pooling hides shapes from the exporter). The engine
# computes the Kaldi fbank itself (filter banks in fbank.bin) and averages several 3 s windows.
FRAMES = 1 + (48000 - 400) // 160


class Campplus(nn.Module):
    def __init__(self, c):
        super().__init__()
        self.c = c

    def forward(self, fbank):
        return self.c(fbank - fbank.mean(dim=1, keepdim=True))


m = Campplus(campplus).eval()
path = os.path.join(OUT, "campplus.onnx")
fb = KaldiFbank()(torch.randn(1, 48000) * 0.1).unsqueeze(0)
with torch.no_grad():
    ref = m(fb)
torch.onnx.export(m, (fb,), path, input_names=["fbank"], output_names=["style"], opset_version=17, dynamo=False)
m.eval()
sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
for _ in range(2):
    w = torch.randn(1, 48000) * 0.1
    full = Style(campplus).eval()
    with torch.no_grad():
        r = full(w).numpy()
    g = sess.run(None, {"fbank": KaldiFbank()(w).unsqueeze(0).numpy()})[0]
    cos = float((r * g).sum() / np.linalg.norm(r) / np.linalg.norm(g))
    print(f"campplus.onnx: rel err {np.abs(g - r).max() / np.abs(r).max():.2e}, cosine {cos:.6f}", flush=True)
print(f"campplus.onnx {os.path.getsize(path) / 1e6:.1f} MB, input [1, {FRAMES}, 80]")
KaldiFbank().banks.numpy().astype("<f4").tofile(os.path.join(OUT, "fbank.bin"))
print("fbank.bin", tuple(KaldiFbank().banks.shape))

import librosa  # noqa: E402

basis = librosa.filters.mel(sr=mel_args["sampling_rate"], n_fft=mel_args["n_fft"], n_mels=mel_args["num_mels"],
                            fmin=mel_args["fmin"], fmax=mel_args["fmax"]).astype("<f4")
basis.tofile(os.path.join(OUT, "melbasis.bin"))
print("melbasis.bin", basis.shape, mel_args)
