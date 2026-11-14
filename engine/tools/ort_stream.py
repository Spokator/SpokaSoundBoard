"""Reference implementation of SpokaSoundBoard's voice engine loop: ONNX Runtime (DirectML) + numpy only.

Mirrors Seed-VC's real-time GUI (block 0.3 s, content context 2.5 s, DiT context 0.5 s, look-ahead 20 ms, SOLA
cross-fade 40 ms) with input at 16 kHz and output at 22.05 kHz. The C# engine is a line-by-line port of this file.
"""
import json
import os
import struct
import sys
import time

import librosa
import numpy as np
import onnxruntime as ort
import soundfile as sf

SR = 22050
HOP = 256
ZC = SR // 50  # 441: one content frame (20 ms) at the output rate


class Voice:
    def __init__(self, path, max_frames=None):
        with open(path, "rb") as fh:
            data = fh.read()
        assert data[:4] == b"SVCV"
        version, frames = struct.unpack("<ii", data[4:12])
        floats = np.frombuffer(data[12:], dtype="<f4")
        self.style = floats[:192].reshape(1, 192)
        self.mel = floats[192:192 + 80 * frames].reshape(1, 80, frames)
        self.prompt = floats[192 + 80 * frames:].reshape(1, frames, 384)
        self.frames = frames
        if max_frames and max_frames < frames:
            self.mel = np.ascontiguousarray(self.mel[:, :, :max_frames])
            self.prompt = np.ascontiguousarray(self.prompt[:, :max_frames])
            self.frames = max_frames


class Engine:
    def __init__(self, model_dir, provider="DmlExecutionProvider", steps=6, cfg_rate=0.7, block_time=0.3, seed=0):
        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        opts.enable_mem_pattern = False  # recommended for DirectML
        opts.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        opts.log_severity_level = 3
        prov = [provider]
        load = lambda n: ort.InferenceSession(os.path.join(model_dir, n), opts, providers=prov)
        self.content, self.regulator, self.dit, self.f0, self.hift = (load(n) for n in
                                                                      ("content.onnx", "regulator.onnx", "dit.onnx", "f0.onnx", "hift.onnx"))
        src = json.load(open(os.path.join(model_dir, "source.json")))
        self.lin_w = np.array(src["linear_weight"], dtype=np.float32)
        self.lin_b = np.float32(src["linear_bias"])
        self.harmonics = src["harmonics"] + 1
        self.window = (0.5 - 0.5 * np.cos(2 * np.pi * np.arange(16) / 16)).astype(np.float32)  # periodic Hann
        self.rng = np.random.default_rng(seed)
        self.steps, self.cfg_rate = steps, cfg_rate

        rnd = lambda s: int(np.round(s * SR / ZC)) * ZC
        self.block = rnd(block_time)
        self.block16 = 320 * self.block // ZC
        self.sola_buffer_len = min(rnd(0.05), 4 * ZC)
        self.sola_search = ZC
        extra = rnd(2.5)
        extra_right = rnd(0.02)
        self.skip_head, self.skip_tail = extra // ZC, extra_right // ZC
        self.return_length = (self.block + self.sola_buffer_len + self.sola_search) // ZC
        self.ce_dit_frames = int(2.0 * 50)
        total = extra + rnd(0.05) + self.sola_search + self.block + extra_right
        self.input16 = np.zeros(320 * total // ZC, dtype=np.float32)
        self.sola_buffer = np.zeros(self.sola_buffer_len, dtype=np.float32)
        t = np.linspace(0.0, 1.0, self.sola_buffer_len, dtype=np.float32)
        self.fade_in = np.sin(0.5 * np.pi * t) ** 2
        self.fade_out = 1 - self.fade_in
        self.voice = None

    # ---- HiFT glue -------------------------------------------------------------------------------------------
    def source(self, f0):
        f = np.repeat(f0[0], HOP).astype(np.float64)  # nearest upsampling to the sample rate
        n = len(f)
        theta = np.empty((self.harmonics, n), dtype=np.float64)
        for i in range(self.harmonics):
            theta[i] = 2 * np.pi * (np.cumsum(f * (i + 1) / SR) % 1)
        phase = self.rng.uniform(-np.pi, np.pi, size=(self.harmonics, 1))
        phase[0] = 0
        sines = 0.1 * np.sin(theta + phase)
        uv = (f > 10).astype(np.float64)
        noise_amp = uv * 0.003 + (1 - uv) * 0.1 / 3
        sines = sines * uv + noise_amp * self.rng.standard_normal(sines.shape)
        merged = np.tanh(self.lin_w @ sines + self.lin_b)
        return merged.astype(np.float32)

    def stft(self, x):
        pad = np.pad(x, (8, 8), mode="reflect")
        frames = 1 + (len(pad) - 16) // 4
        idx = np.arange(16)[None, :] + 4 * np.arange(frames)[:, None]
        spec = np.fft.rfft(pad[idx] * self.window, axis=1)  # [frames, 9]
        return np.concatenate([spec.real.T, spec.imag.T], axis=0)[None].astype(np.float32)  # [1, 18, frames]

    def istft(self, mag, phase):
        spec = (mag * np.cos(phase) + 1j * mag * np.sin(phase)).T  # [frames, 9]
        frames = spec.shape[0]
        y = (np.fft.irfft(spec, n=16, axis=1) * self.window).astype(np.float64)
        n = 16 + 4 * (frames - 1)
        idx = (np.arange(16)[None, :] + 4 * np.arange(frames)[:, None]).ravel()
        out = np.bincount(idx, weights=y.ravel(), minlength=n)
        env = np.bincount(idx, weights=np.tile(self.window.astype(np.float64) ** 2, frames), minlength=n)
        out, env = out[8:-8], env[8:-8]
        return (out / np.where(env > 1e-11, env, 1)).astype(np.float32)

    def vocode(self, mel):
        f0 = self.f0.run(None, {"mel": mel})[0]
        s = self.source(f0)
        x = self.hift.run(None, {"mel": mel, "s_stft": self.stft(s)})[0][0]
        mag = np.minimum(np.exp(x[:9]), 1e2)
        phase = np.sin(x[9:])
        return np.clip(self.istft(mag, phase), -0.99, 0.99)

    # ---- One block ---------------------------------------------------------------------------------------------
    def process(self, block16):
        """block16: block16 samples at 16 kHz -> block samples at 22.05 kHz."""
        v = self.voice
        self.input16[:-self.block16] = self.input16[self.block16:]
        self.input16[-self.block16:] = block16
        s_alt = self.content.run(None, {"wav": self.input16[None]})[0][:, self.ce_dit_frames:]
        target = int((self.skip_head + self.return_length + self.skip_tail - self.ce_dit_frames) / 50 * SR // HOP)
        idx = np.floor(np.arange(target) * (s_alt.shape[1] / target)).astype(np.int64)
        cond = self.regulator.run(None, {"s": np.ascontiguousarray(s_alt[:, idx])})[0]
        mu = np.concatenate([v.prompt, cond], axis=1)
        T = mu.shape[1]
        p = v.frames
        x = self.rng.standard_normal((1, 80, T)).astype(np.float32)
        prompt_x = np.zeros((1, 80, T), dtype=np.float32)
        prompt_x[:, :, :p] = v.mel
        x[:, :, :p] = 0
        zeros_mu = np.zeros_like(mu)
        lens = np.array([T, T], dtype=np.int64)
        style = np.concatenate([v.style, np.zeros_like(v.style)])
        px = np.concatenate([prompt_x, np.zeros_like(prompt_x)])
        mus = np.concatenate([mu, zeros_mu])
        ts = np.linspace(0, 1, self.steps + 1, dtype=np.float32)
        for k in range(1, len(ts)):
            dt = ts[k] - ts[k - 1]
            t = np.array([ts[k - 1]] * 2, dtype=np.float32)
            d = self.dit.run(None, {"x": np.concatenate([x, x]), "prompt_x": px, "x_lens": lens, "t": t, "style": style, "mu": mus})[0]
            dphi = (1.0 + self.cfg_rate) * d[:1] - self.cfg_rate * d[1:]
            x = x + dt * dphi
            x[:, :, :p] = 0
        wave = self.vocode(np.ascontiguousarray(x[:, :, p:]))
        out_len = self.return_length * SR // 50
        tail = self.skip_tail * SR // 50
        infer = wave[-out_len - tail:-tail]
        # SOLA
        n = self.sola_buffer_len
        conv_in = infer[:n + self.sola_search]
        nom = np.correlate(conv_in, self.sola_buffer, mode="valid")
        den = np.sqrt(np.convolve(conv_in ** 2, np.ones(n), mode="valid") + 1e-8)
        offset = int(np.argmax(nom / den))
        infer = infer[offset:].copy()
        infer[:n] = infer[:n] * self.fade_in + self.sola_buffer * self.fade_out
        self.sola_buffer[:] = infer[self.block:self.block + n]
        return infer[:self.block]


def main():
    model_dir, sources, voices, out_dir = sys.argv[1], sys.argv[2].split(","), sys.argv[3].split(","), sys.argv[4]
    provider = sys.argv[5] if len(sys.argv) > 5 else "DmlExecutionProvider"
    steps = int(sys.argv[6]) if len(sys.argv) > 6 else 6
    prompt_s = float(sys.argv[7]) if len(sys.argv) > 7 else 3.0
    os.makedirs(out_dir, exist_ok=True)
    eng = Engine(model_dir, provider=provider, steps=steps)
    print("providers:", eng.dit.get_providers(), flush=True)
    for vname in voices:
        for src in sources:
            eng = Engine(model_dir, provider=provider, steps=steps) if False else eng
            eng.voice = Voice(os.path.join(model_dir, "voices", vname + ".voice"), int(prompt_s * SR / HOP))
            eng.input16[:] = 0
            eng.sola_buffer[:] = 0
            wav, _ = librosa.load(src, sr=16000)
            out, times = [], []
            for pos in range(0, len(wav) - eng.block16, eng.block16):
                t = time.perf_counter()
                out.append(eng.process(wav[pos:pos + eng.block16]))
                times.append(time.perf_counter() - t)
            y = np.concatenate(out)
            name = f"{os.path.splitext(os.path.basename(src))[0]}__to__target_{vname}"
            sf.write(os.path.join(out_dir, name + ".wav"), librosa.resample(y, orig_sr=SR, target_sr=16000), 16000)
            t = np.array(times[3:])
            print(json.dumps({"file": name, "steps": steps, "prompt_s": prompt_s, "ms_mean": round(t.mean() * 1000, 1),
                              "ms_p95": round(np.percentile(t, 95) * 1000, 1), "rtf": round(t.mean() / 0.3, 3)}), flush=True)


if __name__ == "__main__":
    main()
