# CW decoder bench (local, not shipped)

Compares the station-engine's CW decoder (`CwDecoderCore`) with DeepCW
(https://github.com/e04/deepcw-engine, MIT) on synthetic clips with known text.
First run 2026-11-08; results in `results-2026-11-08.json`.

Setup, from this folder (Python 3.14; onnxruntime has no 3.15 wheel yet):

    /opt/homebrew/bin/python3.14 -m venv venv && venv/bin/pip install onnxruntime numpy
    mkdir dl && git clone --depth 1 https://github.com/e04/deepcw-engine.git dl/deepcw-engine
    mkdir enginecw && cp Program.cs enginecw.csproj enginecw/ && (cd enginecw && dotnet build -c Release)
    venv/bin/python -I gen_cw.py clips
    venv/bin/python -I bench.py

`enginecw` builds under the assembly name `SimpleHPSDR.Server.Tests`, the only way to
reach the engine's internal `CwDecoderCore` without changing the engine. It
references the engine project by absolute path (`~/src/SimpleHPSDR/external/station-engine`).

`gen_cw.py`: 48 kHz clips, SNR as DeepCW defines it (average signal power
over the clip against noise in 2500 Hz), then a 500 Hz band-pass at the pitch
like a receiver's CW filter. DeepCW gets a 3200 Hz copy made with ffmpeg.
