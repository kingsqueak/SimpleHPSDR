"""Synthesize CW test clips with known text.

Each clip: 48 kHz mono 16-bit WAV (for the engine decoder) plus a 3200 Hz copy
made with ffmpeg (DeepCW's documented prep), and a line in manifest.jsonl.
SNR is DeepCW's definition: time-averaged signal power over the whole clip
against noise power in 2500 Hz. Audio then goes through a 500 Hz wide band-pass
at the pitch, like a receiver's CW filter, before either decoder sees it.
"""
import json, subprocess, sys, wave
from pathlib import Path
import numpy as np

FS = 48000
MORSE = {
    'A': '.-', 'B': '-...', 'C': '-.-.', 'D': '-..', 'E': '.', 'F': '..-.', 'G': '--.', 'H': '....',
    'I': '..', 'J': '.---', 'K': '-.-', 'L': '.-..', 'M': '--', 'N': '-.', 'O': '---', 'P': '.--.',
    'Q': '--.-', 'R': '.-.', 'S': '...', 'T': '-', 'U': '..-', 'V': '...-', 'W': '.--', 'X': '-..-',
    'Y': '-.--', 'Z': '--..', '0': '-----', '1': '.----', '2': '..---', '3': '...--', '4': '....-',
    '5': '.....', '6': '-....', '7': '--...', '8': '---..', '9': '----.', '/': '-..-.', '?': '..--..',
    '.': '.-.-.-', ',': '--..--',
}
WORDS = ("CQ DE TEST UR RST 599 579 449 5NN NAME QTH OP RIG ANT DIPOLE YAGI PWR 100W 5W WX SUNNY RAIN "
         "COLD TNX FER CALL QSO HW CPY FB OM GE GM GA 73 88 TU AGN PSE QRZ? QSL QRS QRQ BK SK K R "
         "BOSTON CHICAGO DENVER MIAMI SEATTLE FN30 FN42 EM12 DM79 CN87 HERE ES IS THE AND WITH SO "
         "GOOD DX ? 14.025 7.030 NR 001 042 TEMP 15C 2X 3/4 RETIRED TODAY BEST TIME").split()
CALLS = ["KC2RGW", "W1AW", "K3LR", "N0AX", "VE3XYZ", "DL1ABC", "JA1RL", "G4ABC", "AE0Q", "AI5DD", "W6/G0XYZ"]


def make_text(rng, seconds, wpm):
    # PARIS standard: 50 dit units per word; draw enough words for the clip.
    n = max(8, int(seconds * wpm / 60 * 1.1))
    out = []
    for _ in range(n):
        out.append(rng.choice(CALLS) if rng.random() < 0.18 else rng.choice(WORDS))
    return " ".join(out)


def keying(text, wpm, rng, jitter, fs=FS):
    """Envelope (0..1) for text. jitter: SD of each element/gap as a fraction of its length."""
    dit = 1.2 / wpm
    segs = []  # (on?, seconds)

    def j(x):
        return max(x * 0.35, x * (1 + rng.normal(0, jitter))) if jitter else x

    words = text.split(" ")
    for wi, w in enumerate(words):
        for ci, ch in enumerate(w):
            code = MORSE[ch]
            for ei, el in enumerate(code):
                segs.append((True, j(dit if el == '.' else 3 * dit)))
                if ei < len(code) - 1:
                    segs.append((False, j(dit)))
            if ci < len(w) - 1:
                segs.append((False, j(3 * dit)))
        if wi < len(words) - 1:
            segs.append((False, j(7 * dit)))
    segs = [(False, 1.0)] + segs + [(False, 1.5)]
    total = int(sum(s for _, s in segs) * fs) + 1
    env = np.zeros(total, np.float32)
    pos = 0.0
    for on, s in segs:
        a, b = int(pos * fs), int((pos + s) * fs)
        if on:
            env[a:b] = 1
        pos += s
    # 5 ms raised-cosine edges
    k = int(0.005 * fs)
    ramp = 0.5 - 0.5 * np.cos(np.linspace(0, np.pi, k))
    kern = np.concatenate([ramp, ramp[::-1]])
    kern /= kern.sum()
    return np.convolve(env, kern, mode="same").astype(np.float32)


def bandpass(x, lo, hi, fs=FS):
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / fs)
    edge = 60.0
    g = np.clip(np.minimum((f - (lo - edge)) / edge, ((hi + edge) - f) / edge), 0, 1)
    return np.fft.irfft(X * g, len(x)).astype(np.float32)


def write_wav(path, x, fs):
    x = np.clip(x, -1, 1)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(fs)
        w.writeframes((x * 32767).astype("<i2").tobytes())


def clip(name, out, rng, wpm, snr, jitter=0.0, pitch=600.0, qsb_hz=0.0, qsb_db=0.0, seconds=40, drift_hz=0.0):
    text = make_text(rng, seconds, wpm)
    env = keying(text, wpm, rng, jitter)
    t = np.arange(len(env)) / FS
    phase = 2 * np.pi * np.cumsum(pitch + drift_hz * t / t[-1]) / FS
    sig = env * np.sin(phase)
    if qsb_hz:
        depth = 10 ** (-qsb_db / 20)
        sig *= (1 - (1 - depth) * 0.5 * (1 - np.cos(2 * np.pi * qsb_hz * t))).astype(np.float32)
    ps = float(np.mean(sig ** 2))
    pn_2500 = ps / 10 ** (snr / 10)
    sigma = np.sqrt(pn_2500 * (FS / 2) / 2500)
    noisy = sig + rng.normal(0, sigma, len(sig)).astype(np.float32)
    rx = bandpass(noisy, pitch - 250, pitch + 250)
    rx *= 0.5 / max(1e-9, np.percentile(np.abs(rx), 99.9))
    p48 = out / f"{name}.48k.wav"
    write_wav(p48, rx, FS)
    p32 = out / f"{name}.3k2.wav"
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", str(p48), "-ac", "1", "-ar", "3200",
                    "-sample_fmt", "s16", str(p32)], check=True)
    return dict(name=name, wpm=wpm, snr=snr, jitter=jitter, pitch=pitch, qsb_hz=qsb_hz, qsb_db=qsb_db,
                drift_hz=drift_hz, text=text, wav48=str(p48), wav32=str(p32))


def main():
    out = Path(sys.argv[1]); out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(73)
    rows = []
    for wpm in (15, 25, 35):
        for snr in (10, 0, -5, -10):
            for jit, tag in ((0.0, "mach"), (0.15, "hand")):
                rows.append(clip(f"w{wpm}_s{snr:+d}_{tag}", out, rng, wpm, snr, jit))
    rows.append(clip("w25_s+0_qsb", out, rng, 25, 0, 0.05, qsb_hz=0.15, qsb_db=20))
    rows.append(clip("w25_s+5_hand30", out, rng, 25, 5, 0.30))
    rows.append(clip("w20_s+5_pitch750", out, rng, 20, 5, 0.05, pitch=750))
    rows.append(clip("w20_s+5_drift", out, rng, 20, 5, 0.05, drift_hz=60))
    with open(out / "manifest.jsonl", "w") as f:
        for r in rows:
            f.write(json.dumps(r) + "\n")
    print(len(rows), "clips")


if __name__ == "__main__":
    main()
