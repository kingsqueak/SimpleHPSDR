// Benchmark for the signal analyser's AM depth reading: model AM signals of known depth,
// clean and with the things a real band adds (noise, a neighbour 5 kHz away, mistuning,
// fading, static, a carrier that has just come up), are streamed through SignalAnalysis the
// way TCI delivers them, and the reading is taken several times a second as the window does.
// Each case prints the lowest, middle and highest reading against the true depth.
//
//   dotnet run -c Release                       the model signals
//   dotnet run -c Release -- file.f32 rate shiftHz [envelopeHz | tx [highCutHz]]
//                                               a recording of TCI IQ (interleaved float32, as received)
using SimpleHPSDR.Engine;

const double Seconds = 9, SettleSeconds = 4;
var rng = new Random(7);
double Gauss() { double u = 1 - rng.NextDouble(), v = rng.NextDouble(); return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v); }

if (args.Length >= 3)
{
    var bytes = File.ReadAllBytes(args[0]);
    var data = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, data, 0, data.Length * 4);
    int rate = int.Parse(args[1]);
    // "lsb" or "usb" plays it as our own SSB transmission, with the TX high cut after it.
    if (args.Length > 3 && args[3] is "lsb" or "usb")
    {
        var ssb = new SignalAnalysis();
        ssb.Configure(new SignalSetup(SignalKind.Ssb, args[3] == "lsb", args.Length > 4 ? int.Parse(args[4]) : 3000, true));
        ssb.ShiftHz = double.Parse(args[2]);
        int step = rate / 25 * 2, k = 0;
        for (int i = 0; i < data.Length; i += step, k++)
        {
            ssb.Push(data.AsSpan(i, Math.Min(step, data.Length - i)), rate);
            if (k % 75 != 74) continue;                              // every 3 s
            var s = ssb.Snapshot(1, 400, true);
            Console.WriteLine($"t={i / 2.0 / rate:0.0} s: {s.Headline}  (impulses {s.ImpulsesPerSecond:0.0}/s)");
            foreach (var f in s.Findings!) Console.WriteLine($"  {f.Level,-8}{f.Name,-11}{f.Value}");
        }
        return 0;
    }
    // A fourth argument of "tx" plays it as our own transmission, with the TX high cut after it.
    bool own = args.Length > 3 && args[3] == "tx";
    var (up, down, r) = Read(data, rate, double.Parse(args[2]), own
        ? new SignalSetup(SignalKind.Am, false, args.Length > 4 ? int.Parse(args[4]) : 4000, true)
        : new SignalSetup(SignalKind.Am, false, 5000, false, EnvelopeHz: args.Length > 3 ? int.Parse(args[3]) : 0), 2);
    Console.WriteLine($"{args[0]}: up {Range(up)}  down {Range(down)}  snr {r.SnrDb:0} dB  noise alone {r.ModulationNoise * 100:0}%  carrier {r.CarrierOffsetHz:+0;-0} Hz  impulses {r.ImpulsesPerSecond:0.0}/s");
    foreach (var f in r.Findings!) Console.WriteLine($"  {f.Level,-8}{f.Name,-11}{f.Value}");
    return 0;
}

// ---- model signals ----------------------------------------------------------

/// The modulating audio, peak 1: a tone, or something voice-like.
double[] Tone(int n, int rate, double hz) => [.. Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * hz * i / rate))];
double[] Voice(int n, int rate)
{
    var x = new double[n]; double ph = 0;
    for (int i = 0; i < n; i++)
    {
        double t = i / (double)rate, f0 = 120 + 20 * Math.Sin(2 * Math.PI * 0.7 * t);
        ph += 2 * Math.PI * f0 / rate;
        double syll = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 3.1 * t)), 0.6) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.4 * t + 1));
        double s = 0;
        for (int h = 2; h * f0 < 2800; h++) s += Math.Pow(10, -5 * Math.Log2(h * f0 / 300) / 20) * Math.Sin(h * ph + h * h);
        x[i] = s * syll;
    }
    double max = x.Max(Math.Abs);
    for (int i = 0; i < n; i++) x[i] /= max;
    return x;
}

/// AM as the engine's IQ carries it (mirrored): carrier of amplitude `level` at `offsetHz` from the
/// IQ centre, envelope 1 + depth x audio (below zero the carrier reverses, as a real modulator does).
void AddAm(float[] iq, int rate, double offsetHz, double level, double[] audio, double depth, Func<double, double>? gain = null)
{
    for (int i = 0; i < audio.Length; i++)
    {
        double e = 1 + depth * audio[i];
        double a = 2 * Math.PI * offsetHz * i / rate, g = level * e * (gain?.Invoke(i / (double)rate) ?? 1);
        iq[2 * i] += (float)(g * Math.Cos(a));
        iq[2 * i + 1] -= (float)(g * Math.Sin(a));
    }
}
void AddNoise(float[] iq, int rate, double carrier, double cnrDbIn8k)
{
    // Carrier-to-noise in 8 kHz, the usual AM filter: noise power per complex sample for the whole rate.
    double sigma = carrier * Math.Pow(10, -cnrDbIn8k / 20) * Math.Sqrt(rate / 8000.0) / Math.Sqrt(2);
    for (int i = 0; i < iq.Length; i++) iq[i] += (float)(sigma * Gauss());
}

(List<double> Up, List<double> Down, SignalReport Last) Read(float[] iq, int rate, double shiftHz, SignalSetup setup, double settle)
{
    var a = new SignalAnalysis();
    a.Configure(setup);
    a.ShiftHz = shiftHz;
    var up = new List<double>(); var down = new List<double>();
    SignalReport last = new();
    int chunk = rate / 25 * 2, every = 10, k = 0;                // 25 batches a second, a reading every 0.4 s
    for (int i = 0; i < iq.Length; i += chunk, k++)
    {
        a.Push(iq.AsSpan(i, Math.Min(chunk, iq.Length - i)), rate);
        if (k % every != every - 1) continue;
        last = a.Snapshot(1, 400, true);
        if (i / 2.0 / rate < settle) continue;
        up.Add(last.HasDepth && last.ModulationNoise <= 0.3 ? last.ModulationUp * 100 : double.NaN);
        down.Add(last.HasDepth && last.ModulationNoise <= 0.3 ? last.ModulationDown * 100 : double.NaN);
    }
    return (up, down, last);
}
string Range(List<double> v)
{
    var ok = v.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
    if (ok.Count == 0) return "   not measured   ";
    string s = $"{ok[0],4:0} {ok[ok.Count / 2],4:0} {ok[^1],4:0}";
    return ok.Count < v.Count ? s + $" ({v.Count - ok.Count} n/m)" : s + "        ";
}

int failures = 0;
void Case(string name, int rate, double up, double down, Action<float[], int> build, double tolerance = 5,
    int envelopeHz = 4000, double mistuneHz = 0, bool transmit = false, bool mayDecline = false)
{
    const double Offset = 7000;
    var iq = new float[(int)(rate * Seconds) * 2];
    build(iq, rate);
    var setup = new SignalSetup(SignalKind.Am, false, transmit ? 4000 : 5000, transmit, EnvelopeHz: transmit ? 0 : envelopeHz);
    var (u, d, last) = Read(iq, rate, Offset + mistuneHz, setup, SettleSeconds);
    var uo = u.Where(x => !double.IsNaN(x)).ToList(); var dn = d.Where(x => !double.IsNaN(x)).ToList();
    bool declined = uo.Count == 0;
    bool ok = declined ? mayDecline
        : uo.All(x => Math.Abs(x - up) <= tolerance) && dn.All(x => Math.Abs(x - down) <= tolerance);
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name,-44} true +{up,3:0}/-{down,3:0}   up {Range(u)}  down {Range(d)}  noise {last.ModulationNoise * 100,3:0}%");
}
Action<float[], int> Am(double up, double down, double hz = 1000, double cnr = 60, double level = 0.02, Func<double, double>? gain = null) =>
    (iq, rate) => { AddAm(iq, rate, 7000, level, Tone(iq.Length / 2, rate, hz), up, gain); AddNoise(iq, rate, level, cnr); };
// Lopsided audio with no DC, as it leaves a transmitter: a tone and its second harmonic. Upward peaks 1, downward 0.6.
double[] Lopsided(int n, int rate) => [.. Enumerable.Range(0, n).Select(i => { double a = 2 * Math.PI * 500 * i / rate; return (Math.Sin(a) - 0.25 * Math.Cos(2 * a)) / 1.25; })];

Console.WriteLine("lowest / middle / highest reading from 4 s to 9 s, in percent; n/m = declined to measure\n");
foreach (double depth in new[] { 0.1, 0.3, 0.5, 0.8, 0.95, 1.0 })
    Case($"tone 1 kHz, {depth * 100:0}%", 384000, depth * 100, depth * 100, Am(depth, depth));
foreach (int rate in new[] { 48000, 96000, 192000, 768000, 1536000 })
    Case($"tone 80% at {rate / 1000} kHz IQ", rate, 80, 80, Am(0.8, 0.8));
foreach (double hz in new[] { 100.0, 300, 2500, 3500 })
    Case($"tone 80% at {hz:0} Hz audio", 384000, 80, 80, Am(0.8, 0.8, hz));
Case("lopsided +125 / -75 (broadcast style)", 384000, 125, 75, (iq, rate) =>
    { AddAm(iq, rate, 7000, 0.02, Lopsided(iq.Length / 2, rate), 1.25); AddNoise(iq, rate, 0.02, 60); });
Case("over-modulated 130% (carrier reverses)", 384000, 130, 130, Am(1.3, 1.3));
Case("transmit view, tone 90%", 384000, 90, 90, Am(0.9, 0.9, level: 0.06), transmit: true);
Case("transmit view, over-modulated 140%", 384000, 140, 140, Am(1.4, 1.4, level: 0.06), transmit: true);
{
    // The model voice isn't symmetrical, and its tallest peaks are brief: the truth is what it holds in the last 5 s.
    var v = Voice((int)(384000 * Seconds), 384000);
    var tail = v.Skip((int)(384000 * (Seconds - 5))).ToArray();
    Case("voice-like, upward peaks 85%", 384000, 85 * tail.Max(), -85 * tail.Min(), (iq, rate) =>
        { AddAm(iq, rate, 7000, 0.02, v, 0.85); AddNoise(iq, rate, 0.02, 60); }, tolerance: 10);   // its tallest peaks last under half a millisecond
}
foreach (double cnr in new[] { 40.0, 30, 25, 20, 15, 10 })
    Case($"tone 80%, carrier {cnr:0} dB over noise (8 kHz)", 384000, 80, 80, Am(0.8, 0.8, cnr: cnr), tolerance: cnr >= 30 ? 5 : cnr >= 25 ? 8 : 13, mayDecline: cnr <= 20);
foreach (double cnr in new[] { 30.0, 20 })
    Case($"carrier only, {cnr:0} dB over noise", 384000, 0, 0, Am(0, 0, cnr: cnr), tolerance: cnr >= 30 ? 5 : 14, mayDecline: true);
foreach (double rel in new[] { -10.0, 0, 10 })
    Case($"tone 80%, neighbour +5 kHz at {rel:+0;-0;0} dB", 384000, 80, 80, (iq, rate) =>
    {
        Am(0.8, 0.8)(iq, rate);
        AddAm(iq, rate, 12000, 0.02 * Math.Pow(10, rel / 20), Tone(iq.Length / 2, rate, 440), 0.9);
    });
foreach (double off in new[] { 40.0, 150, 600, 1200 })
    Case($"tone 80%, dial {off:0} Hz off the carrier", 384000, 80, 80, Am(0.8, 0.8), mistuneHz: off);
Case("tone 80%, slow fading (0.3 Hz, 6 dB)", 384000, 80, 80, Am(0.8, 0.8, gain: t => 0.75 + 0.25 * Math.Sin(2 * Math.PI * 0.3 * t)), tolerance: 8);
Case("tone 80%, static crashes", 384000, 80, 80, (iq, rate) =>
{
    Am(0.8, 0.8)(iq, rate);
    for (double t = 0.37; t < Seconds; t += 0.61)               // a 2 ms burst, ten times the carrier
        for (int i = (int)(t * rate); i < (int)((t + 0.002) * rate); i++) { iq[2 * i] += (float)(0.2 * Gauss()); iq[2 * i + 1] += (float)(0.2 * Gauss()); }
}, tolerance: 8);
Case("tone 80%, carrier comes up at 3.5 s", 384000, 80, 80, Am(0.8, 0.8, gain: t => t < 3.5 ? 0 : 1), tolerance: 8, mayDecline: true);

Console.WriteLine(failures == 0 ? "\nall within tolerance" : $"\n{failures} outside tolerance");
return failures == 0 ? 0 : 1;
