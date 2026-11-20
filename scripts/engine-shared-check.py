#!/usr/bin/env python3
"""Check shared-stream receivers against a real radio. Receive only.

A shared-stream receiver (receiver 2 fed from receiver 0's DDC stream, receiver
3 from receiver 1's) has no DDC of its own: the engine tunes it inside its
source's stream. This starts a throwaway engine (as engine-check.py does),
connects to the app's last radio, finds a steady carrier, and checks that:
  - a shared receiver tuned 1 kHz below the carrier gives the same 1 kHz tone
    and the same S-meter reading as a receiver with its own DDC on that
    frequency, with its stream's centre 30 kHz above and below it (so the
    shift's sign and size are right);
  - it goes silent and is flagged when the stream moves away, and comes back;
  - receiver 2 works with RX2 off, and receiver 3 from RX2's stream;
  - the shared receivers are fed and nothing is dropped;
  - what a shared receiver costs in engine CPU, beside a receiver with a DDC.
With --retune it also measures the audio a shared receiver loses each time its
source's centre is moved (what dragging the spectrum does).

Usage: scripts/engine-shared-check.py [--carrier HZ] [--retune [STEPS]]
Quit SimpleHPSDR first: only one engine can own the radio.
"""
import argparse, importlib.util, json, math, os, socket, struct, subprocess, sys, tempfile, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


rc = load("rc", "engine-retune-check.py")
ec = rc.ec
TONE = int(rc.TONE_HZ)
OFFSET = 30_000


def post(port, path, body):
    return ec.request(port, "POST", path, body, timeout=20)[0]


def state(port):
    return ec.get_json(port, "api/state")[1]


def row(port, i):
    rs = state(port).get("receivers") or []
    return rs[i] if i < len(rs) else {}


def tone_freq(samples, rate):
    """Frequency of the strongest tone between 800 and 1200 Hz, to 2 Hz."""
    n = min(len(samples), rate // 2)
    if n < rate // 4:
        return 0.0
    best = (0.0, 0.0)
    for f in range(800, 1201, 2):
        w = 2 * math.pi * f / rate
        coeff = 2 * math.cos(w)
        s1 = s2 = 0.0
        for x in samples[:n]:
            s0 = x + coeff * s1 - s2
            s2, s1 = s1, s0
        p = s1 * s1 + s2 * s2 - coeff * s1 * s2
        if p > best[0]:
            best = (p, f)
    return best[1]


class MeterTap(threading.Thread):
    """Latest S-meter readings (dBm, averaged) per receiver from the engine's stream."""

    def __init__(self, port):
        super().__init__(daemon=True)
        self.ws = ec.WebSocket(port)
        self.ws.s.settimeout(0.2)
        self.readings = {}
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
            # RxMetersV2 (0x19, receiver 0) and its per-receiver twin (0x27).
            if op != 2 or len(data) < 34 or data[0] not in (0x19, 0x27):
                continue
            with self.lock:
                self.readings.setdefault(data[33], []).append(struct.unpack_from("<f", data, 5)[0])

    def average(self, seconds=2.0):
        """Mean reading per receiver over the next few seconds."""
        with self.lock:
            self.readings = {}
        time.sleep(seconds)
        with self.lock:
            return {rx: sum(v) / len(v) for rx, v in self.readings.items() if v}

    def close(self):
        self.stop = True
        self.ws.close()


def listen(tap, seconds=1.2):
    """(tone share 0..1, tone level, tone frequency, rms) of what is audible now."""
    time.sleep(0.9)
    tap.take()
    time.sleep(seconds)
    s = tap.take()
    if not s:
        return 0.0, 0.0, 0.0, 0.0
    levels = sorted(rc.tone_levels(s, tap.rate))
    rms = math.sqrt(sum(x * x for x in s) / len(s))
    return rc.tone_purity(s, tap.rate), levels[len(levels) // 2], tone_freq(s, tap.rate), rms


def only(port, audible):
    """Mute every receiver but one."""
    for i in range(4):
        post(port, f"api/receivers/{i}/mute", {"muted": i != audible})


def cpu_seconds(pid):
    out = subprocess.run(["ps", "-o", "time=", "-p", str(pid)], capture_output=True, text=True).stdout.strip()
    parts = out.replace(",", ".").split(":")
    return sum(float(p) * 60 ** k for k, p in enumerate(reversed(parts))) if out else 0.0


def cpu_percent(pid, seconds=6):
    a, t = cpu_seconds(pid), time.time()
    time.sleep(seconds)
    return (cpu_seconds(pid) - a) / (time.time() - t) * 100


def db(a, b):
    return 20 * math.log10(a / b) if a > 0 and b > 0 else -999.0


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--carrier", type=int, help="carrier frequency in Hz (default: try WWV, CHU)")
    ap.add_argument("--retune", type=int, nargs="?", const=60, default=0, metavar="STEPS",
                    help="also measure the audio a shared receiver loses per source retune")
    ap.add_argument("--engine")
    args = ap.parse_args()

    engine = args.engine or os.path.join(ec.ROOT, "artifacts", "engine", "osx-arm64", "StationEngine")
    settings = json.load(open(os.path.join(ec.APP_DATA, "settings.json")))
    if subprocess.run(["pgrep", "-f", "StationEngine"], capture_output=True, text=True).stdout.split():
        sys.exit("another engine is running: quit SimpleHPSDR first")

    work = tempfile.mkdtemp(prefix="shared-check-")
    proc = None
    fails = []

    def check(name, ok, detail):
        print(f"{'ok  ' if ok else 'FAIL'} {name}: {detail}")
        if not ok:
            fails.append(f"{name}: {detail}")

    try:
        proc, port = ec.start_engine(work, engine)
        p2 = settings.get("LastRadioP2", True)
        if not p2:
            sys.exit("shared-stream receivers are Protocol 2 only")
        rate = settings.get("SampleRate", 192000)
        body = {"endpoint": settings["LastRadioIp"], "sampleRate": rate}
        if settings.get("LastRadioBoardId") is not None:
            body["boardId"] = settings["LastRadioBoardId"]
        if post(port, "api/connect/p2", body) >= 300:
            sys.exit("could not connect to the radio")
        time.sleep(2)
        rate = state(port).get("sampleRate") or rate
        print(f"connected, sample rate {rate}")
        post(port, "api/radio/ctun", {"enabled": False})
        for i in (3, 2):
            post(port, f"api/receivers/{i}", {"enabled": False})
        post(port, "api/rx2", {"enabled": False})
        post(port, "api/mode", {"mode": "USB", "receiver": 0})
        post(port, "api/filter", {"lowHz": 300, "highHz": 2700, "receiver": 0})
        only(port, 0)
        tap = rc.AudioTap(port)
        tap.start()
        meters = MeterTap(port)
        meters.start()

        best = (0.0, None)
        for carrier in ([args.carrier] if args.carrier else rc.CARRIERS):
            post(port, "api/vfo", {"hz": carrier - TONE, "receiver": 0})
            purity = listen(tap, 1.0)[0]
            print(f"carrier {carrier / 1e6:.3f} MHz: tone is {purity * 100:.0f}% of the audio")
            if purity > best[0]:
                best = (purity, carrier)
            if purity > 0.8:
                break
        if best[0] < 0.3:
            sys.exit("no steady carrier found; try --carrier with a strong broadcast station")
        carrier = best[1]
        dial = carrier - TONE
        usb = {"mode": "USB", "filterLowHz": 300, "filterHighHz": 2700}

        post(port, "api/vfo", {"hz": dial, "receiver": 0})
        ref = listen(tap)
        print(f"receiver 0 on its own DDC: tone {ref[0] * 100:.0f}%, level {ref[1]:.4f}, {ref[2]:.0f} Hz")
        idle_cpu = cpu_percent(proc.pid)

        # Receiver 2 from receiver 0's stream, RX2 off.
        st = post(port, "api/receivers/2", {"enabled": True, "sourceReceiver": 0, "vfoHz": dial, **usb})
        r2 = row(port, 2)
        check("receiver 2 on", st < 300 and r2.get("enabled") and r2.get("sourceReceiver") == 0
              and not state(port).get("rx2Enabled"),
              f"status {st}, source {r2.get('sourceReceiver')}, rx2Enabled {state(port).get('rx2Enabled')}")
        only(port, 2)
        shared_cpu = cpu_percent(proc.pid)
        # RX2 on its own DDC stays on the carrier as the S-meter reference
        # (audio levels pass through each receiver's AGC, so they don't compare).
        post(port, "api/rx2", {"enabled": True})
        post(port, "api/mode", {"mode": "USB", "receiver": 1})
        post(port, "api/filter", {"lowHz": 300, "highHz": 2700, "receiver": 1})
        post(port, "api/vfo", {"hz": dial, "receiver": 1})
        for off in (0, OFFSET, -OFFSET):
            post(port, "api/vfo", {"hz": dial + off, "receiver": 0})
            got = listen(tap)
            m = meters.average()
            diff = m.get(2, -999) - m.get(1, 999)
            check(f"receiver 2, stream centre {off:+d} Hz from it",
                  got[0] > 0.6 * ref[0] and abs(got[2] - ref[2]) <= 4 and abs(diff) < 2,
                  f"tone {got[0] * 100:.0f}%, {got[2]:.0f} Hz, S-meter {m.get(2, 0):.1f} dBm, "
                  f"{diff:+.1f} dB from RX2's own DDC")

        post(port, "api/vfo", {"hz": dial + 5_000_000, "receiver": 0})
        time.sleep(0.5)
        gone = listen(tap)
        check("receiver 2 out of span", row(port, 2).get("outOfSpan") is True and gone[3] < 0.02 * max(ref[3], 1e-9),
              f"outOfSpan {row(port, 2).get('outOfSpan')}, audio {db(gone[3], ref[3]):+.0f} dB")
        post(port, "api/vfo", {"hz": dial + OFFSET, "receiver": 0})
        time.sleep(1.0)
        back = listen(tap)
        check("receiver 2 back in span", row(port, 2).get("outOfSpan") is False and back[0] > 0.6 * ref[0]
              and abs(back[2] - ref[2]) <= 4,
              f"outOfSpan {row(port, 2).get('outOfSpan')}, tone {back[0] * 100:.0f}%, {back[2]:.0f} Hz")

        if args.retune:
            # The shared receiver's dial stays put; its source's centre steps.
            post(port, "api/rx2", {"enabled": False})
            post(port, "api/radio/ctun", {"enabled": True})
            post(port, "api/vfo", {"hz": dial, "receiver": 0})
            time.sleep(1.5)
            base_lo = state(port)["radioLoHz"]
            for label, target in (("receiver 0 (own DDC)", 0), ("receiver 2 (shared)", 2)):
                only(port, target)
                time.sleep(1.0)
                tap.take()
                time.sleep(1.0)
                quiet = sorted(rc.tone_levels(tap.take(), tap.rate))
                level = quiet[len(quiet) // 2]
                steps = []
                for i in range(args.retune):
                    steps.append(time.time())
                    post(port, "api/receivers/0/lo", {"hz": base_lo + (20_000 if i % 2 == 0 else 0)})
                    time.sleep(0.15)
                time.sleep(0.4)
                samples, end = tap.take_timed()
                post(port, "api/receivers/0/lo", {"hz": base_lo})
                levels = rc.tone_levels(samples, tap.rate)
                win = rc.WIN / tap.rate
                start = end - len(samples) / tap.rate
                bins = [0] * 75
                for k, a in enumerate(levels):
                    if a >= 0.5 * level:
                        continue
                    t = start + (k + 0.5) * win
                    prev = [s for s in steps if s <= t]
                    if prev and t - prev[-1] < 0.150:
                        bins[int((t - prev[-1]) / 0.002)] += 1
                background = sorted(bins)[len(bins) // 2]
                per_step = sum(max(0, b - background) for b in bins) * 2.0 / args.retune
                print(f"RETUNE {label}: {per_step:.1f} ms of disturbed audio per retune "
                      f"({args.retune} steps of 20 kHz, CTUN on)")
            post(port, "api/radio/ctun", {"enabled": False})

        # Receiver 3 from RX2's stream, with receiver 2 off (no cascade, a gap below it).
        post(port, "api/receivers/2", {"enabled": False})
        post(port, "api/vfo", {"hz": dial, "receiver": 0})
        post(port, "api/rx2", {"enabled": True})
        post(port, "api/mode", {"mode": "USB", "receiver": 1})
        post(port, "api/filter", {"lowHz": 300, "highHz": 2700, "receiver": 1})
        post(port, "api/vfo", {"hz": dial, "receiver": 1})
        only(port, 1)
        ref1 = listen(tap)
        print(f"receiver 1 on its own DDC: tone {ref1[0] * 100:.0f}%, level {ref1[1]:.4f}, {ref1[2]:.0f} Hz")
        ddc_cpu = cpu_percent(proc.pid)
        st = post(port, "api/receivers/3", {"enabled": True, "sourceReceiver": 1, "vfoHz": dial, **usb})
        r3 = row(port, 3)
        check("receiver 3 on, receiver 2 off", st < 300 and r3.get("enabled") and r3.get("sourceReceiver") == 1
              and not row(port, 2).get("enabled"),
              f"status {st}, source {r3.get('sourceReceiver')}, receiver 2 enabled {row(port, 2).get('enabled')}")
        only(port, 3)
        for off in (OFFSET, -OFFSET):
            post(port, "api/vfo", {"hz": dial + off, "receiver": 1})
            got = listen(tap)
            m = meters.average()
            diff = m.get(3, -999) - m.get(0, 999)
            check(f"receiver 3, stream centre {off:+d} Hz from it",
                  got[0] > 0.6 * ref1[0] and abs(got[2] - ref1[2]) <= 4 and abs(diff) < 2,
                  f"tone {got[0] * 100:.0f}%, {got[2]:.0f} Hz, S-meter {m.get(3, 0):.1f} dBm, "
                  f"{diff:+.1f} dB from receiver 0's own DDC")

        _, h1 = ec.get_json(port, "api/diagnostics/rx-ingest-health")
        time.sleep(2)
        _, h2 = ec.get_json(port, "api/diagnostics/rx-ingest-health")
        def sec(h, i):
            return next((e for e in (h or {}).get("secondaryRxChannelIds", []) if e.get("receiverIndex") == i), {})
        a, b = sec(h1, 3), sec(h2, 3)
        check("receiver 3 is fed", b.get("fedFrames", 0) > a.get("fedFrames", 0) and b.get("routedFrames") == 0
              and b.get("sourceReceiver") == 1,
              f"fedFrames {a.get('fedFrames')} -> {b.get('fedFrames')}, routedFrames {b.get('routedFrames')}, "
              f"source {b.get('sourceReceiver')}")

        post(port, "api/rx2", {"enabled": False})
        time.sleep(0.5)
        check("RX2 off takes receiver 3 with it", not row(port, 3).get("enabled"),
              f"receiver 3 enabled {row(port, 3).get('enabled')}")
        print(f"CPU, engine process: receiver 0 alone {idle_cpu:.0f}%, plus one shared receiver {shared_cpu:.0f}%, "
              f"plus RX2 on its own DDC {ddc_cpu:.0f}%")

        only(port, 0)
        post(port, "api/receivers/0/mute", {"muted": False})
        tap.close()
        meters.close()
        post(port, "api/disconnect/p2", {})
    finally:
        if proc is not None:
            ec.check_quit(proc, port, os.path.join(work, "engine.log"), fails)
        for f in fails:
            print("FAIL " + f)
        print(f"RESULT: {'all checks passed' if not fails else str(len(fails)) + ' failure(s)'}; "
              f"engine log {os.path.join(work, 'engine.log')}")


if __name__ == "__main__":
    main()
