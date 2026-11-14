"""Exports Seed-VC's real-time model (xlsr-tiny + HiFT) to ONNX for SpokaSoundBoard's voice engine, and checks each
graph against PyTorch.

Graphs (all float32 in/out):
  content.onnx    wav16k [1, N] (raw)  -> S [1, T, 1024]   XLS-R 300M, first 12 layers, input normalized inside
  regulator.onnx  S_interp [1, T', 1024] -> cond [1, T', 384]   (nearest interpolation to T' is done by the engine)
  dit.onnx        x, prompt_x [B, 80, T], x_lens [B] int64, t [B], style [B, 192], mu [B, T, 384] -> [B, 80, T]
  f0.onnx         mel [1, 80, T] -> f0 [1, T]
  hift.onnx       mel [1, 80, T], s_stft [1, 18, F] -> conv_post [1, 18, F']
The engine does the rest: Euler steps with classifier-free guidance, HiFT's sine source (weights in source.json),
its 16-point STFT / inverse STFT, SOLA cross-fades.
"""
import importlib.machinery
import json
import os
import sys
import types

import numpy as np
import onnxruntime as ort
import torch
import torch.nn as nn
import torch.nn.functional as F

ROOT = os.path.dirname(os.path.abspath(__file__))
os.chdir(ROOT)
sys.path.insert(0, ROOT)
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(ROOT), "onnx")
os.makedirs(OUT, exist_ok=True)

gui = importlib.machinery.SourceFileLoader("rtgui", os.path.join(ROOT, "real-time-gui.py")).load_module()
gui.device = torch.device("cpu")
model, semantic_fn, vocoder, campplus, to_mel, mel_args = gui.load_models(
    types.SimpleNamespace(checkpoint_path=None, config_path=None, fp16=False, gpu=0))
torch.manual_seed(0)


# ---- XLS-R (the GUI keeps the first 12 layers and runs it in fp16; we export fp32) ------------------------------
from transformers import Wav2Vec2Model  # noqa: E402

xlsr = Wav2Vec2Model.from_pretrained("facebook/wav2vec2-xls-r-300m", attn_implementation="eager")  # SDPA exports wrongly
xlsr.encoder.layers = xlsr.encoder.layers[:12]
xlsr.eval()
# The positional convolution uses a weight-norm parametrization, which the exporter bakes wrongly: fold it first.
from torch.nn.utils import parametrize  # noqa: E402

for module in xlsr.modules():
    if parametrize.is_parametrized(module):
        for name in list(module.parametrizations.keys()):
            parametrize.remove_parametrizations(module, name, leave_parametrized=True)


class Content(nn.Module):
    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, wav):
        x = (wav - wav.mean(dim=-1, keepdim=True)) / torch.sqrt(wav.var(dim=-1, keepdim=True, unbiased=False) + 1e-7)
        return self.m(x).last_hidden_state


class Regulator(nn.Module):
    def __init__(self, lr):
        super().__init__()
        self.lr = lr

    def forward(self, s_interp):
        x = self.lr.content_in_proj(s_interp).transpose(1, 2)
        x = self.lr.model(x)
        return x.transpose(1, 2)


class Dit(nn.Module):
    def __init__(self, est):
        super().__init__()
        self.est = est

    def forward(self, x, prompt_x, x_lens, t, style, mu):
        return self.est(x, prompt_x, x_lens, t, style, mu)


class F0(nn.Module):
    def __init__(self, v):
        super().__init__()
        self.v = v

    def forward(self, mel):
        return self.v.f0_predictor(mel)


class Hift(nn.Module):
    def __init__(self, v):
        super().__init__()
        self.v = v

    def forward(self, mel, s_stft):
        v = self.v
        x = v.conv_pre(mel)
        for i in range(v.num_upsamples):
            x = F.leaky_relu(x, v.lrelu_slope)
            x = v.ups[i](x)
            if i == v.num_upsamples - 1:
                x = v.reflection_pad(x)
            si = v.source_downs[i](s_stft)
            si = v.source_resblocks[i](si)
            x = x + si
            xs = None
            for j in range(v.num_kernels):
                r = v.resblocks[i * v.num_kernels + j](x)
                xs = r if xs is None else xs + r
            x = xs / v.num_kernels
        x = F.leaky_relu(x)
        return v.conv_post(x)


def export(module, args, name, inputs, outputs, axes):
    path = os.path.join(OUT, name)
    # Reference first, in eval mode: the exporter restores the wrapper's (training) mode afterwards, which would
    # turn dropout back on in the reference.
    module.eval()
    with torch.no_grad():
        ref = module(*args)
    torch.onnx.export(module, args, path, input_names=inputs, output_names=outputs, dynamic_axes=axes,
                      opset_version=17, do_constant_folding=True, dynamo=False)
    module.eval()
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    got = sess.run(None, {n: a.numpy() for n, a in zip(inputs, args)})[0]
    err = float(np.max(np.abs(got - ref.numpy())))
    scale = float(np.max(np.abs(ref.numpy())))
    print(f"{name}: {os.path.getsize(path) / 1e6:.1f} MB, max |onnx - torch| = {err:.2e} (output scale {scale:.2f})", flush=True)
    return err / max(scale, 1e-6)


report = {}
wav = torch.randn(1, 16000 * 3) * 0.1
report["content"] = export(Content(xlsr), (wav,), "content.onnx", ["wav"], ["features"], {"wav": {1: "n"}, "features": {1: "t"}})

lr = model.length_regulator
s_interp = torch.randn(1, 120, 1024)
report["regulator"] = export(Regulator(lr), (s_interp,), "regulator.onnx", ["s"], ["cond"], {"s": {1: "t"}, "cond": {1: "t"}})

est = model.cfm.estimator
# « x_lens + self.style_as_token + self.time_as_token » adds booleans to a tensor, which the tracer rejects.
est.style_as_token = int(est.style_as_token)
est.time_as_token = int(est.time_as_token)
T = 300
args = (torch.randn(2, 80, T), torch.randn(2, 80, T), torch.tensor([T, T]), torch.tensor([0.3, 0.3]), torch.randn(2, 192),
        torch.randn(2, T, 384))
report["dit"] = export(Dit(est), args, "dit.onnx", ["x", "prompt_x", "x_lens", "t", "style", "mu"], ["dphi"],
                       {"x": {0: "b", 2: "t"}, "prompt_x": {0: "b", 2: "t"}, "x_lens": {0: "b"}, "t": {0: "b"},
                        "style": {0: "b"}, "mu": {0: "b", 1: "t"}, "dphi": {0: "b", 2: "t"}})

vocoder.eval()
mel = torch.randn(1, 80, 60) * 2 - 6
report["f0"] = export(F0(vocoder), (mel,), "f0.onnx", ["mel"], ["f0"], {"mel": {2: "t"}, "f0": {1: "t"}})
with torch.no_grad():
    s = vocoder._f02source(vocoder.f0_predictor(mel))
    re, im = vocoder._stft(s.squeeze(1))
    s_stft = torch.cat([re, im], dim=1)
report["hift"] = export(Hift(vocoder), (mel, s_stft), "hift.onnx", ["mel", "s_stft"], ["x"],
                        {"mel": {2: "t"}, "s_stft": {2: "f"}, "x": {2: "f2"}})

# HiFT's harmonic source: Linear(9 -> 1) then tanh, plus the SineGen settings.
src = vocoder.m_source
json.dump({
    "linear_weight": src.l_linear.weight.detach().flatten().tolist(),
    "linear_bias": float(src.l_linear.bias.detach()),
    "harmonics": src.l_sin_gen.harmonic_num, "sine_amp": src.l_sin_gen.sine_amp, "noise_std": src.l_sin_gen.noise_std,
    "voiced_threshold": src.l_sin_gen.voiced_threshold, "sampling_rate": src.l_sin_gen.sampling_rate,
    "upsample": int(np.prod(vocoder.ups.__len__() and [8, 8]) * vocoder.istft_params["hop_len"]),
    "n_fft": vocoder.istft_params["n_fft"], "hop": vocoder.istft_params["hop_len"], "audio_limit": vocoder.audio_limit,
    "mel": {k: v for k, v in mel_args.items()},
}, open(os.path.join(OUT, "source.json"), "w"), indent=1)
print(json.dumps(report))
