#!/usr/bin/env python3
"""Repeatable check of the station engine, before and after engine changes.

Starts a throwaway engine (its own data folder, no computer audio), then:
  1. routes:  every engine path the app's source uses must exist. Each is probed
              with a made-up HTTP method, so ASP.NET answers 405 (route exists)
              or 404 (missing) without running any handler.
  2. reads:   every path the app only GETs is read, and the status recorded.
  3. stream:  the /ws WebSocket opens and its frame types are counted.
  4. radio:   (only with --radio) connects to the radio RECEIVE ONLY, checks
              spectrum and audio frames, changes mode, filter and frequency
              and reads them back, then disconnects. No transmit route is
              ever called.
  order:      (only with --order) posts several settings changes at the same
              moment, many times, and checks the DSP side always ends on the
              newest one.
  5. quit:    POST /api/engine/shutdown; the engine must exit by itself
              within 15 s with no "failed to stop" or fatal lines in its log.

Results go to a JSON file; with --baseline they're compared with an earlier
run and every difference is printed. Exit code 0 = no failures, no differences.

Usage:
  scripts/engine-check.py [--radio [IP]] [--baseline FILE] [--out FILE]
The radio must not be owned by another engine (quit SimpleHPSDR first).
"""
import argparse, base64, glob, http.client, json, os, re, shutil, signal, socket
import struct, subprocess, sys, tempfile, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP_DATA = os.path.expanduser("~/Library/Application Support/SimpleHPSDR")
LOG_BAD = re.compile(r"failed to stop|fatal|Unhandled exception", re.I)
# Frame types (SimpleHPSDR.Contracts/MsgType.cs).
FRAME_NAMES = {0x01: "Display", 0x02: "AudioPcm", 0x19: "RxMetersV2", 0x26: "VfoState"}


# ---------------------------------------------------------------- app paths

def app_paths():
    """Engine paths in the app's C# source -> set of methods seen with each."""
    found = {}
    files = [f for f in glob.glob(os.path.join(ROOT, "src", "**", "*.cs"), recursive=True)
             if "/bin/" not in f and "/obj/" not in f]
    lit = re.compile(r'\$?"/?(api/[^"]*)"')
    for f in files:
        for line in open(f, encoding="utf-8"):
            for raw in lit.findall(line):
                path = raw.split("?")[0]
                # {rx} -> 0, any other placeholder -> a harmless word.
                path = re.sub(r"\{(rx|receiver|i|index)\}", "0", path)
                path = re.sub(r"\{[^}]*\}", "probe", path)
                if "Get" in line and not re.search(r"Post|Put|Delete|Patch|Send", line):
                    method = "GET"
                else:
                    method = "OTHER"
                found.setdefault(path, set()).add(method)
    return found


# ---------------------------------------------------------------- http

def request(port, method, path, body=None, timeout=10):
    c = http.client.HTTPConnection("127.0.0.1", port, timeout=timeout)
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if data is not None else {}
    try:
        c.request(method, "/" + path, body=data, headers=headers)
        r = c.getresponse()
        return r.status, r.read()
    finally:
        c.close()


def get_json(port, path):
    status, data = request(port, "GET", path)
    return status, (json.loads(data) if data and status == 200 else None)


# ---------------------------------------------------------------- websocket

class WebSocket:
    """Just enough of RFC 6455 for a binary-frame client."""

    def __init__(self, port, path="/ws"):
        self.s = socket.create_connection(("127.0.0.1", port), timeout=5)
        key = base64.b64encode(os.urandom(16)).decode()
        self.s.sendall((f"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
                        "Upgrade: websocket\r\nConnection: Upgrade\r\n"
                        f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
        head = b""
        while b"\r\n\r\n" not in head:
            chunk = self.s.recv(1)
            if not chunk:
                raise ConnectionError("WebSocket handshake closed")
            head += chunk
        if b" 101 " not in head.split(b"\r\n")[0]:
            raise ConnectionError(head.split(b"\r\n")[0].decode())

    def send(self, payload):
        mask = os.urandom(4)
        n = len(payload)
        hdr = bytes([0x82]) + (bytes([0x80 | n]) if n < 126 else bytes([0xFE]) + struct.pack(">H", n))
        self.s.sendall(hdr + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))

    def _read(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.s.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("WebSocket closed")
            buf += chunk
        return buf

    def recv(self):
        b0, b1 = self._read(2)
        n = b1 & 0x7F
        if n == 126:
            n = struct.unpack(">H", self._read(2))[0]
        elif n == 127:
            n = struct.unpack(">Q", self._read(8))[0]
        if b1 & 0x80:
            self._read(4)
        return b0 & 0x0F, self._read(n)

    def count_frames(self, seconds):
        counts = {}
        end = time.time() + seconds
        self.s.settimeout(0.5)
        while time.time() < end:
            try:
                op, data = self.recv()
            except socket.timeout:
                continue
            if op == 2 and data:
                name = FRAME_NAMES.get(data[0], f"0x{data[0]:02x}")
                counts[name] = counts.get(name, 0) + 1
            elif op == 8:
                break
        return counts

    def close(self):
        try:
            self.s.close()
        except OSError:
            pass


# ---------------------------------------------------------------- engine

def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_for_wisdom(port, limit_s=1200):
    """A new WDSP library rebuilds its FFT plans on first start (minutes), and
    the DSP doesn't run until that is done. Wait for it."""
    told = False
    end = time.time() + limit_s
    while time.time() < end:
        _, d = get_json(port, "api/station/dsp-diagnostics")
        phase = (d or {}).get("wdspWisdomPhase")
        if phase not in ("Idle", "Building"):
            return
        if not told:
            print("engine is building its FFT plans (new WDSP library); waiting...", flush=True)
            told = True
        time.sleep(2)
    raise RuntimeError("FFT plans were not ready in time")


def start_engine(workdir, engine):
    data = os.path.join(workdir, "data")
    os.makedirs(data)
    # Reuse the FFT wisdom so the throwaway engine doesn't spend minutes building it.
    for name in ("wdspWisdom01", "wdspWisdom01.version"):
        src = os.path.join(APP_DATA, name)
        if os.path.exists(src):
            shutil.copy(src, data)
    port = free_port()
    env = dict(os.environ,
               SIMPLEHPSDR_PREFS_PATH=os.path.join(data, "prefs.db"),
               STATION_ENGINE_DATA_DIR=data,
               STATION_ENGINE_FIREWALL_RULE="SimpleHPSDR engine check",
               STATION_ENGINE_TCI_DEVICE="SimpleHPSDR check")
    env["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning"
    log = open(os.path.join(workdir, "engine.log"), "w")
    proc = subprocess.Popen([engine, "--port", str(port), "--native-audio-output", "false"],
                            cwd=os.path.dirname(engine), env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        deadline = time.time() + 180
        while time.time() < deadline:
            if proc.poll() is not None:
                raise RuntimeError(f"engine exited with code {proc.returncode}")
            try:
                if request(port, "GET", "api/station/version", timeout=2)[0] == 200:
                    wait_for_wisdom(port)
                    return proc, port
            except OSError:
                pass
            time.sleep(0.25)
        raise RuntimeError("engine did not answer within 180 s")
    except BaseException:
        # Only ever stop the process this script started, through its own Popen object.
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(5)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
        raise


# ---------------------------------------------------------------- checks

def check_routes(port, paths):
    out = {}
    for path in sorted(paths):
        out[path] = request(port, "PROBE", path)[0]
    return out


def check_reads(port, paths):
    out = {}
    for path in sorted(p for p, m in paths.items() if m == {"GET"}):
        try:
            out[path] = request(port, "GET", path, timeout=15)[0]
        except OSError as e:
            out[path] = f"error: {e}"
    return out


def check_stream(port, seconds):
    ws = WebSocket(port)
    try:
        ws.send(bytes([0x22, 2]))   # this session wants the display stream
        ws.send(bytes([0x21, 0]))   # and audio
        return ws.count_frames(seconds)
    finally:
        ws.close()


def check_order(port, rounds, fails, threads=8):
    """Many settings changes at the same moment must leave the DSP side on the
    newest one. Each round posts several different dial frequencies at once,
    then compares the engine's record (/api/state) with the last frequency the
    DSP pipeline announced on the stream (VfoState frame)."""
    import threading
    ws = WebSocket(port)
    ws.send(bytes([0x22, 2]))
    ws.s.settimeout(0.05)
    last = {"hz": None}

    def drain(seconds):
        end = time.time() + seconds
        while time.time() < end:
            try:
                op, data = ws.recv()
            except socket.timeout:
                continue
            if op == 2 and len(data) >= 18 and data[0] == 0x26 and data[1] == 0:
                last["hz"] = struct.unpack_from("<q", data, 2)[0]

    conns = [http.client.HTTPConnection("127.0.0.1", port, timeout=10) for _ in range(threads)]
    stale, examples = 0, []
    try:
        # Warm-up: the pipeline only records the very first frequency it sees.
        for hz in (6_999_000, 6_999_500):
            request(port, "POST", "api/vfo", {"hz": hz, "receiver": 0})
        drain(0.3)
        for r in range(rounds):
            barrier = threading.Barrier(threads)

            def post(i, r=r, barrier=barrier):
                body = json.dumps({"hz": 7_000_000 + r * 100 + i, "receiver": 0})
                barrier.wait()
                conns[i].request("POST", "/api/vfo", body=body, headers={"Content-Type": "application/json"})
                conns[i].getresponse().read()

            ts = [threading.Thread(target=post, args=(i,)) for i in range(threads)]
            for t in ts: t.start()
            for t in ts: t.join()
            drain(0.08)
            _, state = get_json(port, "api/state")
            want = state and state.get("vfoHz")
            if last["hz"] != want:
                drain(0.3)   # give a slow push every chance before calling it stale
            if last["hz"] != want:
                stale += 1
                if len(examples) < 3:
                    examples.append({"state": want, "dsp": last["hz"]})
    finally:
        for c in conns: c.close()
        ws.close()
    if stale:
        fails.append(f"order: DSP left on an older setting in {stale} of {rounds} rounds, e.g. {examples[0]}")
    return {"rounds": rounds, "stale": stale, "examples": examples}


def check_radio(port, ip, settings, fails):
    """Receive only. Connect, check frames, change mode/filter/VFO, disconnect."""
    r = {}
    p2 = settings.get("LastRadioP2", True)
    body = {"endpoint": ip, "sampleRate": settings.get("SampleRate", 192000)}
    if settings.get("LastRadioBoardId") is not None:
        body["boardId"] = settings["LastRadioBoardId"]
    status, data = request(port, "POST", "api/connect/p2" if p2 else "api/connect", body, timeout=20)
    r["connect"] = status
    if status >= 300:
        fails.append(f"radio: connect answered {status}: {data[:200]!r}")
        return r
    time.sleep(2)
    _, state = get_json(port, "api/state")
    r["status"] = state and state.get("status")
    frames = check_stream(port, 4)
    r["frames"] = frames
    for need in ("Display", "AudioPcm"):
        if not frames.get(need):
            fails.append(f"radio: no {need} frames in 4 s")

    def set_and_read(label, path, body, field, want):
        st = request(port, "POST", path, body)[0]
        time.sleep(0.5)
        _, s = get_json(port, "api/state")
        got = s and s.get(field)
        r[label] = {"status": st, "got": got}
        if st >= 300 or got != want:
            fails.append(f"radio: {label} answered {st}, state {field}={got!r}, wanted {want!r}")

    orig = state or {}
    set_and_read("mode", "api/mode", {"mode": "AM", "receiver": 0}, "mode", "AM")
    set_and_read("filter", "api/filter", {"lowHz": -4000, "highHz": 4000, "receiver": 0}, "filterHighHz", 4000)
    set_and_read("vfo", "api/vfo", {"hz": 7_200_000, "receiver": 0}, "vfoHz", 7_200_000)
    # Put things back so the next real session starts where the operator left it.
    # (Throwaway prefs, so this only matters for the radio itself, which keeps nothing.)
    if orig.get("vfoHz"):
        request(port, "POST", "api/vfo", {"hz": orig["vfoHz"], "receiver": 0})
    r["disconnect"] = request(port, "POST", "api/disconnect/p2" if p2 else "api/disconnect", {}, timeout=20)[0]
    return r


def check_quit(proc, port, logpath, fails):
    t0 = time.time()
    try:
        status = request(port, "POST", "api/engine/shutdown", {}, timeout=5)[0]
    except OSError as e:
        status = f"error: {e}"
    try:
        proc.wait(15)
        took = round(time.time() - t0, 1)
    except subprocess.TimeoutExpired:
        proc.kill()
        fails.append("quit: engine still running 15 s after shutdown")
        took = None
    bad = [l.strip() for l in open(logpath, errors="replace") if LOG_BAD.search(l)]
    if bad:
        fails.append(f"quit: {len(bad)} bad log line(s), first: {bad[0][:160]}")
    return {"shutdown": status, "exitCode": proc.returncode, "seconds": took, "badLogLines": bad[:20]}


# ---------------------------------------------------------------- compare

def compare(old, new, prefix=""):
    diffs = []
    for k in sorted(set(old) | set(new)):
        a, b = old.get(k, "<absent>"), new.get(k, "<absent>")
        if isinstance(a, dict) and isinstance(b, dict):
            diffs += compare(a, b, f"{prefix}{k}.")
        elif a != b:
            diffs.append(f"{prefix}{k}: {a} -> {b}")
    return diffs


def comparable(res):
    """Only what should stay the same between runs (not frame counts or timings)."""
    out = {"routes": res["routes"], "reads": res["reads"],
           "streamTypes": {k: True for k in res["stream"]}}
    if "radio" in res:
        radio = dict(res["radio"])
        radio["frames"] = {k: True for k in radio.get("frames", {})}
        out["radio"] = radio
    out["quit"] = {"shutdown": res["quit"]["shutdown"], "exitCode": res["quit"]["exitCode"]}
    return out


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--radio", nargs="?", const="", metavar="IP",
                    help="also run the receive-only radio check (default: the app's last radio)")
    ap.add_argument("--baseline", help="compare with this earlier result file")
    ap.add_argument("--out", help="write results here (default: a file in the work folder)")
    ap.add_argument("--engine", help="StationEngine executable (default: artifacts/engine/<rid>)")
    ap.add_argument("--order", type=int, nargs="?", const=300, default=0, metavar="ROUNDS",
                    help="also stress the order of settings changes (default 300 rounds)")
    ap.add_argument("--keep", action="store_true", help="keep the work folder")
    args = ap.parse_args()

    engine = args.engine or os.path.join(ROOT, "artifacts", "engine", "osx-arm64", "StationEngine")
    if not os.path.exists(engine):
        sys.exit(f"no engine at {engine}; run scripts/build-engine.sh")
    settings = {}
    try:
        settings = json.load(open(os.path.join(APP_DATA, "settings.json")))
    except (OSError, ValueError):
        pass
    if args.radio is not None:
        ip = args.radio or settings.get("LastRadioIp")
        if not ip:
            sys.exit("--radio needs an IP (no last radio in settings.json)")
        running = subprocess.run(["pgrep", "-f", "StationEngine"], capture_output=True, text=True).stdout.split()
        if running:
            sys.exit(f"another engine is running (pid {', '.join(running)}): quit SimpleHPSDR first")

    work = tempfile.mkdtemp(prefix="engine-check-")
    fails, res = [], {}
    proc = None
    try:
        proc, port = start_engine(work, engine)
        _, ver = get_json(port, "api/station/version")
        res["version"] = ver
        paths = app_paths()
        res["routes"] = check_routes(port, paths)
        for p, st in res["routes"].items():
            if st == 404:
                fails.append(f"route missing: {p}")
        res["reads"] = check_reads(port, paths)
        for p, st in res["reads"].items():
            if not isinstance(st, int) or st >= 500:
                fails.append(f"read failed: {p} -> {st}")
        res["stream"] = check_stream(port, 3)
        if args.order:
            res["order"] = check_order(port, args.order, fails)
        if args.radio is not None:
            res["radio"] = check_radio(port, ip, settings, fails)
    finally:
        if proc is None:
            pass
        elif proc.poll() is None:
            res["quit"] = check_quit(proc, port, os.path.join(work, "engine.log"), fails)
        else:
            fails.append(f"engine died during the check (code {proc.returncode})")
            res["quit"] = {"shutdown": None, "exitCode": proc.returncode}

    res["fails"] = fails
    out = args.out or os.path.join(work, "result.json")
    json.dump(res, open(out, "w"), indent=1, sort_keys=True)

    routes = res["routes"]
    print(f"engine {ver and ver.get('engine')}: {len(routes)} app routes, "
          f"{sum(1 for s in routes.values() if s != 404)} present; "
          f"{len(res['reads'])} reads; stream {res['stream']}")
    if "order" in res:
        print(f"order: {res['order']['stale']} stale of {res['order']['rounds']} rounds")
    if "radio" in res:
        print(f"radio: {json.dumps(res['radio'])}")
    print(f"quit: {res['quit']['shutdown']} in {res['quit'].get('seconds')} s, exit {res['quit']['exitCode']}")
    diffs = []
    if args.baseline:
        diffs = compare(comparable(json.load(open(args.baseline))), comparable(res))
        print(f"against {args.baseline}: {len(diffs)} difference(s)")
        for d in diffs:
            print("  " + d)
    for f in fails:
        print("FAIL " + f)
    print(f"results: {out}; engine log: {os.path.join(work, 'engine.log')}")
    if not args.keep and not fails and not diffs and args.out:
        shutil.rmtree(work, ignore_errors=True)
    sys.exit(1 if fails or diffs else 0)


if __name__ == "__main__":
    main()
