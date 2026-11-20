# signal-bench

A benchmark for the Signal Analyser's AM modulation reading (`src/SimpleHPSDR/Engine/SignalAnalysis.cs`),
for when there is no signal generator to hand. It makes model AM signals of known depth, clean and with
what a real band adds (noise, a neighbour 5 kHz away, a dial off the carrier, fading, static, a carrier
that has just come up), streams them through the analyser as TCI would, and takes the reading several
times a second as the window does. Each line shows the lowest, middle and highest reading against the truth.

    export PATH=/usr/local/share/dotnet:$PATH
    dotnet run -c Release

It exits non-zero if a case is outside its tolerance. The tolerances are 5 points on clean signals and
wider where noise sets the limit (see the noisy-carrier cases): the analyser marks such readings "about",
and declines to read once noise alone would show more than 30%.

A recording of real TCI IQ (interleaved float32 as received, which is mirrored) can be replayed:

    dotnet run -c Release -- recording.f32 <rate> <dial minus IQ centre, Hz> [RX filter half-width, Hz]
