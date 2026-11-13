#!/usr/bin/env python3
"""Measure how much receive audio a retune disturbs. Receive only.

Starts a throwaway engine (as engine-check.py does), connects to the app's
last radio, finds a steady carrier, tunes 1 kHz below it in USB with CTUN on,
and listens to the 1 kHz tone on the engine's audio stream while it moves the
radio's centre frequency back and forth (what dragging the spectrum does).
The dial never moves, so a perfect engine would leave the tone untouched.
Reported: milliseconds of disturbed tone per retune step.

Usage: scripts/engine-retune-check.py [--steps 60] [--step-hz 20000] [--carrier HZ]
Quit SimpleHPSDR first: only one engine can own the radio.
"""
import argparse, importlib.util, json, math, os, socket, struct, subprocess, sys, tempfile, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("ec", os.path.join(HERE, "engine-check.py"))
ec = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ec)

CARRIERS = [10_000_000, 15_000_000, 5_000_000, 7_850_000, 14_670_000, 3_330_000, 20_000_000, 2_500_000]
TONE_HZ = 1000.0
WIN = 96            # 2 ms at 48 kHz


class AudioTap(threading.Thread):
    """Collects RX1 audio samples from the engine's stream."""

    def __init__(self, port):
        super().__init__(daemon=True)
        self.ws = ec.WebSocket(port)
        self.ws.send(bytes([0x22, 2]))
        self.ws.send(bytes([0x21, 0]))
        self.ws.s.settimeout(0.2)
        self.samples = []
        self.frames = []        # (arrival time, samples so far) per audio frame
        self.total = 0
        self.rate = 48000
        self.lock = threading.Lock()
        self.stop = False

    def run(self):
        while not self.stop:
            try:
                op, data = self.ws.recv()
            except socket.timeout:
                continue
            except (ConnectionError, OSError):
                return
            if op != 2 or len(data) < 24 or data[0] != 0x02 or data[16] != 0:
                continue
            channels = data[17]
            self.rate, count = struct.unpack_from("<IH", data, 18)
            floats = struct.unpack_from(f"<{count * channels}f", data, 24)
            with self.lock:
                self.samples.extend(floats[::channels])
                self.total += count
                self.frames.append((time.time(), self.total))

    def take(self):
        with self.lock:
            out, self.samples = self.samples, []
            self.frames, self.total = [], 0
        return out

    def take_timed(self):
        """Samples plus the arrival time of the last one."""
        with self.lock:
            out, self.samples = self.samples, []
            end = self.frames[-1][0] if self.frames else time.time()
            self.frames, self.total = [], 0
        return out, end

    def close(self):
        self.stop = True
        self.ws.close()


def tone_levels(samples, rate):
    """Amplitude of the 1 kHz tone in consecutive 2 ms windows (Goertzel)."""
    w = 2 * math.pi * TONE_HZ / rate
    coeff = 2 * math.cos(w)
    out = []
    for i in range(0, len(samples) - WIN + 1, WIN):
        s1 = s2 = 0.0
        for x in samples[i:i + WIN]:
            s0 = x + coeff * s1 - s2
            s2, s1 = s1, s0
        power = s1 * s1 + s2 * s2 - coeff * s1 * s2
        out.append(math.sqrt(max(power, 0.0)) * 2 / WIN)
    return out


def tone_purity(samples, rate):
    """Share of the audio power that is the 1 kHz tone, 0..1."""
    if len(samples) < rate // 4:
        return 0.0
    levels = tone_levels(samples, rate)
    total = sum(x * x for x in samples) / len(samples)
    tone = sum(a * a for a in levels) / len(levels) / 2
    return min(1.0, tone / total) if total > 0 else 0.0


def post(port, path, body):
    return ec.request(port, "POST", path, body, timeout=20)[0]


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--steps", type=int, default=60)
    ap.add_argument("--step-hz", type=int, default=20_000)
    ap.add_argument("--carrier", type=int, help="carrier frequency in Hz (default: try WWV, CHU)")
    ap.add_argument("--engine")
    args = ap.parse_args()

    engine = args.engine or os.path.join(ec.ROOT, "artifacts", "engine", "osx-arm64", "StationEngine")
    settings = json.load(open(os.path.join(ec.APP_DATA, "settings.json")))
    if subprocess.run(["pgrep", "-f", "StationEngine"], capture_output=True, text=True).stdout.split():
        sys.exit("another engine is running: quit SimpleHPSDR first")

    work = tempfile.mkdtemp(prefix="retune-check-")
    proc = None
    fails = []
    try:
        proc, port = ec.start_engine(work, engine)
        body = {"endpoint": settings["LastRadioIp"], "sampleRate": settings.get("SampleRate", 192000)}
        if settings.get("LastRadioBoardId") is not None:
            body["boardId"] = settings["LastRadioBoardId"]
        if post(port, "api/connect/p2" if settings.get("LastRadioP2", True) else "api/connect", body) >= 300:
            sys.exit("could not connect to the radio")
        time.sleep(2)
        post(port, "api/radio/ctun", {"enabled": True})
        post(port, "api/mode", {"mode": "USB", "receiver": 0})
        post(port, "api/filter", {"lowHz": 300, "highHz": 2700, "receiver": 0})
        tap = AudioTap(port)
        tap.start()

        best = (0.0, None)
        for carrier in ([args.carrier] if args.carrier else CARRIERS):
            post(port, "api/vfo", {"hz": carrier - int(TONE_HZ), "receiver": 0})
            time.sleep(1.2)
            tap.take()
            time.sleep(1.0)
            purity = tone_purity(tap.take(), tap.rate)
            print(f"carrier {carrier / 1e6:.3f} MHz: tone is {purity * 100:.0f}% of the audio")
            if purity > best[0]:
                best = (purity, carrier)
            if purity > 0.8:
                break
        if best[0] < 0.3:
            sys.exit("no steady carrier found; try --carrier with a strong broadcast station")
        carrier = best[1]
        post(port, "api/vfo", {"hz": carrier - int(TONE_HZ), "receiver": 0})
        time.sleep(1.5)
        _, state = ec.get_json(port, "api/state")
        base_lo = state["radioLoHz"]

        tap.take()
        time.sleep(1.0)
        quiet = tone_levels(tap.take(), tap.rate)
        quiet.sort()
        level = quiet[len(quiet) // 2]

        step_times = []
        for i in range(args.steps):
            step_times.append(time.time())
            post(port, "api/receivers/0/lo", {"hz": base_lo + (args.step_hz if i % 2 == 0 else 0)})
            time.sleep(0.15)
        time.sleep(0.4)
        samples, end_time = tap.take_timed()
        levels = tone_levels(samples, tap.rate)
        post(port, "api/receivers/0/lo", {"hz": base_lo})
        tap.close()

        # Fold the tone level onto the time since each retune. Fading and the
        # station's own modulation land anywhere in the 150 ms between steps;
        # what a retune disturbs lands at the same offset every time.
        win_s = WIN / tap.rate
        start_time = end_time - len(samples) / tap.rate
        bins = [0] * 75                                  # 2 ms bins over 150 ms
        for k, a in enumerate(levels):
            if a >= 0.5 * level:
                continue
            t = start_time + (k + 0.5) * win_s
            # the retune this window follows
            lo, hi = 0, len(step_times)
            while lo < hi:
                mid = (lo + hi) // 2
                if step_times[mid] <= t: lo = mid + 1
                else: hi = mid
            if lo == 0:
                continue
            off = t - step_times[lo - 1]
            if 0 <= off < 0.150:
                bins[int(off / 0.002)] += 1
        background = sorted(bins)[len(bins) // 2]
        excess = [max(0, b - background) for b in bins]
        per_step = sum(excess) * 2.0 / args.steps
        peak = max(range(len(bins)), key=lambda i: bins[i])
        print(f"carrier {carrier / 1e6:.3f} MHz, {args.steps} retunes of {args.step_hz} Hz, tone level {level:.4f}")
        print("low-tone windows by time after the retune (10 ms columns): "
              + " ".join(str(sum(bins[i:i + 5])) for i in range(0, 75, 5)))
        print(f"background {background} per 2 ms bin; busiest at {peak * 2} ms after the retune")
        print(f"RESULT: {per_step:.1f} ms of disturbed audio per retune")
        post(port, "api/disconnect/p2" if settings.get("LastRadioP2", True) else "api/disconnect", {})
    finally:
        if proc is not None:
            ec.check_quit(proc, port, os.path.join(work, "engine.log"), fails)
        for f in fails:
            print("FAIL " + f)


if __name__ == "__main__":
    main()
