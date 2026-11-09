"""Run DeepCW (wide model, the example's streaming decoder) and the engine's
CwDecoderCore over the clips in manifest.jsonl and score them.

CER: edit distance / reference length, on text with whitespace collapsed.
CER-ns: the same with every space removed, so word-gap mistakes don't count.
"""
import json, re, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).parent
EXAMPLE = HERE / "dl/deepcw-engine/examples/python/decode_morse.py"
ENGINE = HERE / "enginecw/bin/Release/net10.0/Zeus.Server.Tests.dll"


def norm(s):
    return re.sub(r"\s+", " ", s.upper()).strip()


def lev(a, b):
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def cer(ref, hyp, spaces=True):
    r, h = norm(ref), norm(hyp)
    if not spaces:
        r, h = r.replace(" ", ""), h.replace(" ", "")
    return 100.0 * lev(r, h) / max(1, len(r))


def deepcw(wav32, model):
    meta = model.with_suffix(".onnx.json")
    t = time.process_time()
    p = subprocess.run([str(HERE / "venv/bin/python"), "-I", str(EXAMPLE), "--model", str(model),
                        "--metadata", str(meta), "--wav", wav32], capture_output=True, text=True,
                       cwd=EXAMPLE.parent)
    if p.returncode:
        sys.exit(p.stderr)
    return p.stdout


def main():
    rows = [json.loads(l) for l in open(HERE / "clips/manifest.jsonl")]
    eng = subprocess.run(["dotnet", str(ENGINE)] + [r["wav48"] for r in rows], capture_output=True, text=True, check=True)
    eng_text = {}
    for line in eng.stdout.splitlines():
        name, _, text = line.split("\t", 2)
        eng_text[name.replace(".48k.wav", "")] = text
    model = HERE / "dl/deepcw-engine/model.onnx"
    out = []
    print(f"{'clip':22} {'engine CER':>10} {'(no sp)':>8} {'DeepCW CER':>11} {'(no sp)':>8}")
    for r in rows:
        d = deepcw(r["wav32"], model)
        e = eng_text[r["name"]]
        row = dict(name=r["name"], ref=r["text"], engine=norm(e), deepcw=norm(d),
                   e_cer=cer(r["text"], e), e_ns=cer(r["text"], e, False),
                   d_cer=cer(r["text"], d), d_ns=cer(r["text"], d, False))
        out.append(row)
        print(f"{r['name']:22} {row['e_cer']:10.1f} {row['e_ns']:8.1f} {row['d_cer']:11.1f} {row['d_ns']:8.1f}", flush=True)
    json.dump(out, open(HERE / "results.json", "w"), indent=1)


if __name__ == "__main__":
    main()
