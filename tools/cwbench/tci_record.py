"""Record receive audio from a TCI server to a WAV file, for the CW decoder bench.

Receive only: it asks for audio_start on one receiver and never sends any
transmit, tune or frequency command. Also saves the VFO and mode it saw at
the start, next to the WAV (.json).

    venv/bin/python -I tci_record.py --seconds 120 --out rec/40m-ragchew.wav
"""
import argparse, asyncio, json, struct, time, wave
from pathlib import Path

import numpy as np
import websockets

STREAM_RX_AUDIO = 1


async def record(host, port, rx, seconds, out):
    url = f"ws://{host}:{port}"
    chunks, info = [], {"started": time.strftime("%Y-%m-%d %H:%M:%S")}
    async with websockets.connect(url, max_size=None) as ws:
        # Wait for the handshake to finish (the server ends it with ready;).
        while True:
            msg = await ws.recv()
            if isinstance(msg, str):
                for cmd in msg.split(";"):
                    name, _, args = cmd.strip().partition(":")
                    if name in ("vfo", "modulation", "rx_filter_band", "cw_macros_speed"):
                        info[f"{name}:{args.split(',')[0]}" if name != "cw_macros_speed" else name] = args
                    if name == "ready":
                        break
                else:
                    continue
                break
        await ws.send("audio_samplerate:48000;")
        await ws.send(f"audio_start:{rx};")
        rate, got, t_end = 48000, 0, time.monotonic() + seconds
        while time.monotonic() < t_end:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=2)
            except asyncio.TimeoutError:
                continue
            if isinstance(msg, str) or len(msg) < 64:
                continue
            receiver, srate, fmt, _codec, _crc, length, stype = struct.unpack_from("<7I", msg, 0)
            if stype != STREAM_RX_AUDIO or receiver != rx or fmt != 3:
                continue
            data = np.frombuffer(msg, "<f4", count=length, offset=64)
            chunks.append(data[0::2] if len(data) >= 2 else data)  # stereo: keep the left channel
            rate = srate
            got += len(data) // 2
        await ws.send(f"audio_stop:{rx};")
    audio = np.concatenate(chunks) if chunks else np.zeros(0, np.float32)
    peak = float(np.max(np.abs(audio))) if len(audio) else 0.0
    scaled = audio / peak * 0.7 if peak > 0 else audio
    out.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(out), "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
        w.writeframes((np.clip(scaled, -1, 1) * 32767).astype("<i2").tobytes())
    info.update(rate=rate, seconds=round(len(audio) / rate, 1), peak=peak)
    out.with_suffix(".json").write_text(json.dumps(info, indent=1))
    print(f"{out}: {info['seconds']} s at {rate} Hz, VFO/mode {[v for k, v in info.items() if ':' in k]}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=50001)  # this station; the engine default is 40001
    ap.add_argument("--rx", type=int, default=0)
    ap.add_argument("--seconds", type=float, default=120)
    ap.add_argument("--out", type=Path, required=True)
    a = ap.parse_args()
    asyncio.run(record(a.host, a.port, a.rx, a.seconds, a.out))


if __name__ == "__main__":
    main()
