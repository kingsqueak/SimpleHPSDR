using System.Globalization;
using System.Numerics;

namespace SimpleHPSDR.Engine;

public enum SignalKind { Ssb, Am, Other }

/// <summary>How a reading is marked: grey, green, blue, amber, red.</summary>
public enum FindingLevel { None, Good, Info, Caution, Bad }

/// <summary>What is being looked at. <paramref name="HighHz"/> is the highest audio frequency
/// the channel should hold (the filter's outer edge); <paramref name="Transmit"/> means the
/// signal is our own, heard by the receiver while keyed.</summary>
public sealed record SignalSetup(SignalKind Kind, bool LowerSideband, int HighHz, bool Transmit,
    bool TestTone = false, double MicPeakDbfs = double.NaN, double AlcDb = double.NaN, int EnvelopeHz = 0);

public sealed record Finding(FindingLevel Level, string Name, string Value, string Advice);

/// <summary>One look at the signal: the envelope for the scope, the spectrum round the carrier,
/// and the readings with their advice.</summary>
public sealed class SignalReport
{
    /// <summary>The envelope's lowest and highest value in each column, oldest first (full scale = 1).</summary>
    public float[] EnvMin = [], EnvMax = [];
    public double ScopeSeconds;
    /// <summary>The highest envelope of the last few seconds, and the AM carrier (0 when there is none).</summary>
    public double Peak, Carrier;
    /// <summary>Averaged and peak-hold power in dBFS per bin, from <see cref="SpectrumLowHz"/> upwards, relative to the dial.</summary>
    public float[] SpectrumDb = [], SpectrumPeakDb = [];
    public double SpectrumLowHz, BinHz;
    /// <summary>Where the signal belongs, relative to the dial.</summary>
    public double ChannelLowHz, ChannelHighHz;
    /// <summary>AM depth as fractions (1 = 100%): upward and downward peaks, the average, and what
    /// noise alone would read. <see cref="HasDepth"/> is false while there is no steady carrier.</summary>
    public double ModulationUp, ModulationDown, ModulationAverage, ModulationNoise;
    public bool HasDepth;
    public double SnrDb, CarrierOffsetHz, ImpulsesPerSecond;
    public SignalSetup? Setup;
    public IReadOnlyList<Finding>? Findings;
    public FindingLevel Overall;
    public string Headline = "";
}

/// <summary>
/// Measures an SSB or AM signal from raw IQ: tunes to it, narrows to 24 kHz, and keeps the
/// envelope, an averaged spectrum and a few seconds of statistics. From those come the
/// readings a modulation monitor gives (peaks, density, modulation depth, bandwidth,
/// energy outside the channel) and plain advice on what they mean. It only listens.
/// IQ arrives on the TCI thread; <see cref="Snapshot"/> is called from the UI.
/// </summary>
public sealed class SignalAnalysis
{
    private const int WorkRate = 24000;
    private const int FftSize = 2048, Hop = FftSize / 2;
    private const int ChannelTaps = 129;
    private const int Blocks = 100;                 // 5 s of 50 ms blocks
    private const int HistBins = 400;               // 0.25 dB steps, -100..0 dBFS
    private const double HistStepDb = 0.25, HistFloorDb = -100;
    private const double ScopeMaxSeconds = 5.5;
    private const double GuardHz = 300, AdjacentHz = 3000;

    private readonly object _lock = new();
    private SignalSetup _setup = new(SignalKind.Ssb, false, 3000, false);
    private double _shiftHz;

    // Front end: conjugate (the engine's IQ is mirrored), mix the dial to zero, low-pass, decimate.
    private int _inRate, _decim = 1;
    private double _fs = WorkRate;
    private float[] _lp = [], _histRe = [], _histIm = [];
    private int _histPos, _decimCount;
    private double _ncoRe = 1, _ncoIm, _ncoShift = double.NaN;
    private double _stepRe = 1, _stepIm;
    private int _ncoNorm;
    private float _rawPeak;

    // Impulse blanker, on the IQ as it arrives. A spike a few samples long and many times the
    // level round it (static, a switching click, a glitch in the receiver) is tiny in energy but
    // sets every peak reading. The samples run 24 late so a spike can be seen whole: it must be
    // over within a few samples, or it is the signal itself coming up. A blanked stretch repeats
    // the last good sample after tuning (where the wanted signal stands nearly still), not before
    // it: held as it arrives, a signal off the centre of the IQ would be notched out for the stretch.
    private const int BlankRing = 64, BlankDelay = 24, BlankReach = 6, BlankLongest = 10;
    private const double BlankOver = 36;            // power: six times the amplitude round it
    private readonly float[] _blankRe = new float[BlankRing], _blankIm = new float[BlankRing];
    private readonly bool[] _blankHot = new bool[BlankRing];
    private long _blankIn;
    private int _blankHotCount, _blankBlock;
    private bool _blanking, _blankHotOut;
    private double _blankLevel;
    private float _goodRe, _goodIm;
    private readonly int[] _blanked = new int[Blocks];
    private readonly float[] _rawPeaks = new float[Blocks];

    // Channel filter and envelope.
    private float[] _chRe = [], _chIm = [], _chHistRe = [], _chHistIm = [];
    private int _chPos;
    private float[] _env = [];
    private long _envCount;
    private int _skip;

    // 50 ms blocks.
    private int _blockLen = 1200, _blockFill;
    private double _blockSum, _blockSumSq;
    private float _blockMax, _blockMin = float.MaxValue;
    private readonly ushort[][] _blockHist = new ushort[Blocks][];
    private readonly int[] _hist = new int[HistBins];
    private readonly Block[] _blocks = new Block[Blocks];
    private int _blockNext, _blockCount;
    private double _carrierQuiet, _carrierLoud;
    private bool _haveQuiet, _haveLoud;

    private struct Block { public float Mean, Max, Min, AcRms, Carrier, ModRms; public bool Valid; }

    // AM: the carrier is followed in frequency and phase, and the depth is what lies in phase
    // with it. Against that, noise counts half, a carrier 5 kHz away doesn't count at all, and
    // downward peaks that pass 100% show as such.
    private const int ModBins = 500;                // 1% steps, -200%..+300%
    private const double ModFloor = -2.0, ModStep = 0.01;
    private const double CarrierLoopHz = 12;
    private readonly ushort[][] _blockMod = new ushort[Blocks][];
    private readonly int[] _mod = new int[ModBins];
    private double _carrierHz, _carNcoRe = 1, _carNcoIm, _carStepRe = 1, _carStepIm, _carAlpha;
    private double _c1r, _c1i, _c2r, _c2i, _c3r, _c3i, _carStartRe, _carStartIm;
    private double _blockCar, _blockModSq, _envHalf, _blockWide;
    private readonly double[] _outside = new double[Blocks], _outsideSort = new double[Blocks];
    private const double DepthTail = 10000;         // the peaks are the levels passed one part in this of the time
    private const double DepthTailSigma = 3.72;     // and noise alone passes this many of its own sigmas as often
    private int _blockModN, _carSettle;
    private bool _carrierFound;

    // Spectrum.
    private readonly float[] _fftRe = new float[FftSize], _fftIm = new float[FftSize];
    private readonly float[] _winRe = new float[FftSize], _winIm = new float[FftSize];
    private int _fftFill;
    private readonly float[] _window = new float[FftSize], _cos = new float[FftSize / 2], _sin = new float[FftSize / 2];
    private readonly double _windowSum;
    private readonly double[] _avg = new double[FftSize], _peak = new double[FftSize], _frame = new double[FftSize];
    private bool _haveAvg;
    private double _channelPeak, _outsideUsual;

    public SignalAnalysis()
    {
        for (int i = 0; i < Blocks; i++) { _blockHist[i] = new ushort[HistBins]; _blockMod[i] = new ushort[ModBins]; }
        double sum = 0;
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize));
            sum += _window[i];
        }
        _windowSum = sum;
        for (int i = 0; i < FftSize / 2; i++)
        {
            _cos[i] = (float)Math.Cos(2 * Math.PI * i / FftSize);
            _sin[i] = (float)Math.Sin(2 * Math.PI * i / FftSize);
        }
        BuildChannel();
        Reset();
    }

    /// <summary>The rate the IQ arrives at; 0 until some has.</summary>
    public int InputRate { get { lock (_lock) return _inRate; } }

    /// <summary>How far the signal's dial is above the centre of the IQ, in Hz.</summary>
    public double ShiftHz
    {
        get { lock (_lock) return _shiftHz; }
        set { lock (_lock) _shiftHz = value; }
    }

    /// <summary>Say what the signal is. A change of kind, sideband, width or direction starts the readings again.</summary>
    public void Configure(SignalSetup setup)
    {
        lock (_lock)
        {
            var old = _setup;
            _setup = setup;
            if (old.Kind == setup.Kind && old.LowerSideband == setup.LowerSideband && old.HighHz == setup.HighHz
                && old.Transmit == setup.Transmit && old.TestTone == setup.TestTone && old.EnvelopeHz == setup.EnvelopeHz) return;
            BuildChannel();
            Reset();
        }
    }

    /// <summary>Start the readings again (the dial moved).</summary>
    public void Restart() { lock (_lock) Reset(); }

    private void Reset()
    {
        _env = new float[(int)(_fs * ScopeMaxSeconds)];
        _envCount = 0;
        _skip = (int)(_fs * 0.2);          // the change-over itself isn't the signal
        _blockLen = Math.Max(64, (int)(_fs / 20));
        _blockFill = 0;
        (_blockSum, _blockSumSq, _blockMax, _blockMin, _blockWide) = (0, 0, 0, float.MaxValue, 0);
        Array.Clear(_outside);
        Array.Clear(_hist);
        foreach (var h in _blockHist) Array.Clear(h);
        Array.Clear(_mod);
        foreach (var h in _blockMod) Array.Clear(h);
        (_c1r, _c1i, _c2r, _c2i, _c3r, _c3i, _carStartRe, _carStartIm) = (0, 0, 0, 0, 0, 0, 0, 0);
        (_blockCar, _blockModSq, _blockModN, _carSettle, _carrierFound) = (0, 0, 0, 0, false);
        _carAlpha = 1 - Math.Exp(-2 * Math.PI * CarrierLoopHz / _fs);
        SetCarrier(0);
        Array.Clear(_blocks);
        Array.Clear(_rawPeaks);
        Array.Clear(_blanked);
        _blankBlock = 0;
        (_blockNext, _blockCount, _rawPeak) = (0, 0, 0);
        (_haveQuiet, _haveLoud, _carrierQuiet, _carrierLoud) = (false, false, 0, 0);
        _fftFill = 0;
        _haveAvg = false;
        (_channelPeak, _outsideUsual) = (0, 0);
        Array.Clear(_avg);
        Array.Clear(_peak);
    }

    // ---- where the signal belongs ------------------------------------------

    private int High => Math.Clamp(_setup.HighHz, 1000, 9000);

    /// <summary>The channel relative to the dial: one sideband for SSB, both for everything else.</summary>
    private (double Low, double High) Channel() => _setup.Kind == SignalKind.Ssb
        ? _setup.LowerSideband ? (-High, 0) : (0, High)
        : (-High, High);

    private void BuildChannel()
    {
        var (lo, hi) = Channel();
        double centre = (lo + hi) / 2, half = (hi - lo) / 2 + 500;
        // Our own signal has no neighbours, so its envelope keeps everything it radiates:
        // narrowing a clipped signal to its channel would round the flat tops off again.
        if (_setup.Transmit) (centre, half) = (0, Math.Min(High + 3500, _fs * 0.45));
        // A received AM signal has neighbours 5 kHz away (broadcast) or closer: the depth is
        // measured inside the listener's own filter, never wider than keeps those out.
        else if (_setup.Kind == SignalKind.Am)
            half = Math.Clamp(_setup.EnvelopeHz > 0 ? _setup.EnvelopeHz : High, 2500, 4200);
        _envHalf = half;
        if (_chRe.Length != ChannelTaps)
        {
            _chRe = new float[ChannelTaps];
            _chIm = new float[ChannelTaps];
            _chHistRe = new float[ChannelTaps * 2];
            _chHistIm = new float[ChannelTaps * 2];
        }
        var proto = LowPass(ChannelTaps, half / _fs);
        int mid = ChannelTaps / 2;
        for (int j = 0; j < ChannelTaps; j++)
        {
            // Taps stored against the history's order (oldest first).
            int k = ChannelTaps - 1 - j;
            double a = 2 * Math.PI * centre * (k - mid) / _fs;
            _chRe[j] = (float)(proto[k] * Math.Cos(a));
            _chIm[j] = (float)(proto[k] * Math.Sin(a));
        }
    }

    /// <summary>A windowed-sinc low-pass with unity gain at zero; <paramref name="cutoff"/> is a fraction of the sample rate.</summary>
    private static float[] LowPass(int taps, double cutoff)
    {
        var h = new float[taps];
        int mid = taps / 2;
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - mid;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = (float)(sinc * w);
            sum += h[i];
        }
        for (int i = 0; i < taps; i++) h[i] = (float)(h[i] / sum);
        return h;
    }

    // ---- IQ in --------------------------------------------------------------

    /// <summary>Interleaved IQ at <paramref name="rate"/>, as TCI delivers it.</summary>
    public void Push(ReadOnlySpan<float> iq, int rate)
    {
        if (rate < 8000) return;
        lock (_lock)
        {
            if (rate != _inRate) SetRate(rate);
            if (_shiftHz != _ncoShift)
            {
                _ncoShift = _shiftHz;
                double step = -2 * Math.PI * _shiftHz / rate;
                (_stepRe, _stepIm) = (Math.Cos(step), Math.Sin(step));
            }
            int taps = _lp.Length;
            for (int i = 0; i + 1 < iq.Length; i += 2)
            {
                float mag = Math.Max(Math.Abs(iq[i]), Math.Abs(iq[i + 1]));
                if (mag > _rawPeak) _rawPeak = mag;
                if (!Blank(iq[i], -iq[i + 1], out float re, out float im, out bool spike)) continue;
                float mr = (float)(re * _ncoRe - im * _ncoIm), mi = (float)(re * _ncoIm + im * _ncoRe);
                if (spike) (mr, mi) = (_goodRe, _goodIm);
                else if (!_blankHotOut) (_goodRe, _goodIm) = (mr, mi);
                double nr = _ncoRe * _stepRe - _ncoIm * _stepIm;
                _ncoIm = _ncoRe * _stepIm + _ncoIm * _stepRe;
                _ncoRe = nr;
                if (++_ncoNorm >= 4096)
                {
                    _ncoNorm = 0;
                    double g = 1 / Math.Sqrt(_ncoRe * _ncoRe + _ncoIm * _ncoIm);
                    _ncoRe *= g;
                    _ncoIm *= g;
                }
                _histRe[_histPos] = _histRe[_histPos + taps] = mr;
                _histIm[_histPos] = _histIm[_histPos + taps] = mi;
                if (++_histPos == taps) _histPos = 0;
                if (++_decimCount < _decim) continue;
                _decimCount = 0;
                OnSample(Dot(_lp, _histRe.AsSpan(_histPos, taps)), Dot(_lp, _histIm.AsSpan(_histPos, taps)));
            }
        }
    }

    /// <summary>Takes a sample in and gives the one from 24 samples back, with spikes taken out.
    /// False until 24 have arrived.</summary>
    private bool Blank(float inRe, float inIm, out float re, out float im, out bool blank)
    {
        blank = false;
        int w = (int)(_blankIn & (BlankRing - 1));
        if (_blankHot[w]) _blankHotCount--;                     // the slot being reused leaves the count
        double power = (double)inRe * inRe + (double)inIm * inIm;
        bool hot = _blankLevel > 0 && power > _blankLevel * BlankOver;
        // The level round it, over about 20 ms. Anything strong counts for no more than four times
        // the level: a spike barely moves it, a crash of static lasting milliseconds stays "hot" all
        // the way through (so it is seen as long, and left to the block check), and a signal that
        // has really come up still pulls the level up within a few tens of milliseconds.
        if (_blankLevel <= 0) _blankLevel = power;
        else _blankLevel += (Math.Min(power, _blankLevel * 4) - _blankLevel) * Math.Min(1, 50.0 / _inRate);
        (_blankRe[w], _blankIm[w], _blankHot[w]) = (inRe, inIm, hot);
        if (hot) _blankHotCount++;
        _blankIn++;
        (re, im) = (0, 0);
        if (_blankIn <= BlankDelay) return false;

        long o = _blankIn - 1 - BlankDelay;
        int r = (int)(o & (BlankRing - 1));
        if (_blankHotCount > 0)
        {
            // Near a hot sample? Then it is a spike only if all of that is over quickly.
            int near = 0, run = 0;
            for (long j = o - BlankReach; j <= o + BlankReach; j++)
                if (j >= 0 && _blankHot[(int)(j & (BlankRing - 1))]) near++;
            if (near > 0)
            {
                for (long j = Math.Max(0, o - BlankDelay); j <= o + BlankDelay; j++)
                    if (_blankHot[(int)(j & (BlankRing - 1))]) run++;
                blank = run <= BlankLongest;
            }
        }
        if (blank && !_blanking) _blankBlock++;
        (re, im, _blankHotOut) = (_blankRe[r], _blankIm[r], _blankHot[r]);
        _blanking = blank;
        return true;
    }

    private void SetRate(int rate)
    {
        _inRate = rate;
        _decim = Math.Max(1, (int)Math.Round(rate / (double)WorkRate));
        _fs = rate / (double)_decim;
        // Clean to 10 kHz either side, and nothing folds back inside that.
        int taps = _decim == 1 ? 1 : (int)(3.4 * rate / 4000) | 1;
        _lp = _decim == 1 ? [1f] : LowPass(taps, 12000.0 / rate);
        _histRe = new float[taps * 2];
        _histIm = new float[taps * 2];
        (_histPos, _decimCount) = (0, 0);
        _ncoShift = double.NaN;
        BuildChannel();
        Reset();
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Vector<float>.Count, i = 0;
        var acc = Vector<float>.Zero;
        for (; i <= a.Length - n; i += n) acc += new Vector<float>(a[i..]) * new Vector<float>(b[i..]);
        float sum = Vector.Sum(acc);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private void OnSample(float re, float im)
    {
        // The channel filter is fed even while the change-over is being skipped, so it has settled by the end.
        _chHistRe[_chPos] = _chHistRe[_chPos + ChannelTaps] = re;
        _chHistIm[_chPos] = _chHistIm[_chPos + ChannelTaps] = im;
        if (++_chPos == ChannelTaps) _chPos = 0;
        if (_skip > 0) { _skip--; return; }

        _fftRe[_fftFill] = re;
        _fftIm[_fftFill] = im;
        if (++_fftFill == FftSize)
        {
            SpectrumFrame();
            Array.Copy(_fftRe, Hop, _fftRe, 0, FftSize - Hop);
            Array.Copy(_fftIm, Hop, _fftIm, 0, FftSize - Hop);
            _fftFill = FftSize - Hop;
        }

        var hr = _chHistRe.AsSpan(_chPos, ChannelTaps);
        var hi = _chHistIm.AsSpan(_chPos, ChannelTaps);
        float yr = Dot(_chRe, hr) - Dot(_chIm, hi), yi = Dot(_chRe, hi) + Dot(_chIm, hr);
        float e = MathF.Sqrt(yr * yr + yi * yi);

        _blockWide += (double)re * re + (double)im * im;
        if (_setup.Kind == SignalKind.Am) Depth(yr, yi);
        _env[(int)(_envCount++ % _env.Length)] = e;
        _blockSum += e;
        _blockSumSq += (double)e * e;
        if (e > _blockMax) _blockMax = e;
        if (e < _blockMin) _blockMin = e;
        int bin = e <= 1e-5f ? 0 : Math.Clamp((int)((20 * MathF.Log10(e) - HistFloorDb) / HistStepDb), 0, HistBins - 1);
        _blockHist[_blockNext][bin]++;
        if (++_blockFill >= _blockLen) CloseBlock();
    }

    private void SetCarrier(double hz)
    {
        _carrierHz = hz;
        double step = -2 * Math.PI * hz / _fs;
        (_carStepRe, _carStepIm) = (Math.Cos(step), Math.Sin(step));
    }

    /// <summary>One sample of the channel in AM: bring the carrier to rest, smooth it to a few hertz
    /// (the carrier alone), and take the signal's part in phase with it, as a fraction of it.</summary>
    private void Depth(float yr, float yi)
    {
        double ur = yr * _carNcoRe - yi * _carNcoIm, ui = yr * _carNcoIm + yi * _carNcoRe;
        double nr = _carNcoRe * _carStepRe - _carNcoIm * _carStepIm;
        _carNcoIm = _carNcoRe * _carStepIm + _carNcoIm * _carStepRe;
        _carNcoRe = nr;
        _c1r += _carAlpha * (ur - _c1r); _c1i += _carAlpha * (ui - _c1i);
        _c2r += _carAlpha * (_c1r - _c2r); _c2i += _carAlpha * (_c1i - _c2i);
        _c3r += _carAlpha * (_c2r - _c3r); _c3i += _carAlpha * (_c2i - _c3i);
        double c2 = _c3r * _c3r + _c3i * _c3i;
        if (c2 < 1e-18) return;
        double m = (ur * _c3r + ui * _c3i) / c2 - 1;
        _blockModSq += m * m;
        _blockCar += Math.Sqrt(c2);
        _blockModN++;
        _blockMod[_blockNext][Math.Clamp((int)((m - ModFloor) / ModStep), 0, ModBins - 1)]++;
    }

    /// <summary>The end of a block in AM: keep its depth only if the carrier was there and steady
    /// (not keying, not in a fade), and pull the carrier's frequency in from how far its phase turned.</summary>
    private void CloseCarrierBlock(ref Block b)
    {
        double g = 1 / Math.Sqrt(_carNcoRe * _carNcoRe + _carNcoIm * _carNcoIm);
        _carNcoRe *= g;
        _carNcoIm *= g;
        double start = Math.Sqrt(_carStartRe * _carStartRe + _carStartIm * _carStartIm), end = Math.Sqrt(_c3r * _c3r + _c3i * _c3i);
        double turn = start > 0 && end > 0
            ? Math.Atan2(_carStartRe * _c3i - _carStartIm * _c3r, _carStartRe * _c3r + _carStartIm * _c3i) : 0;
        // Static: a crash is wideband, so the power outside the channel jumps with it. Such blocks are
        // left out, as a noise blanker would. Not for our own signal, whose splatter rises with the voice.
        double outside = Math.Max(0, _blockWide - _blockSumSq) / _blockFill;
        _outside[_blockNext] = outside;
        int have = Math.Min(_blockCount + 1, Blocks);
        Array.Copy(_outside, _outsideSort, Blocks);
        Array.Sort(_outsideSort);                   // blocks not yet filled are zero and sort first
        double usual = _outsideSort[Blocks - have + have / 2];
        bool crash = !_setup.Transmit && have >= 10 && outside > usual * 4 && outside > 0.02 * _blockSumSq / _blockFill;
        bool steady = _carrierFound && !crash && _carSettle >= 4 && _blockModN > 0 && start > 0
            && end / start is > 0.85 and < 1.18 && Math.Abs(turn) < 0.6;
        if (steady)
        {
            b.Valid = true;
            b.Carrier = (float)(_blockCar / _blockModN);
            b.ModRms = (float)Math.Sqrt(_blockModSq / _blockModN);
            if (b.ModRms < 0.02) { _carrierQuiet = _haveQuiet ? _carrierQuiet + (b.Carrier - _carrierQuiet) * 0.2 : b.Carrier; _haveQuiet = true; }
            else if (b.ModRms > 0.15) { _carrierLoud = _haveLoud ? _carrierLoud + (b.Carrier - _carrierLoud) * 0.2 : b.Carrier; _haveLoud = true; }
        }
        else Array.Clear(_blockMod[_blockNext]);
        if (_carrierFound && start > 0 && end > 0)
            SetCarrier(_carrierHz + 0.3 * turn / (2 * Math.PI * _blockFill / _fs));
        _carSettle++;
        (_carStartRe, _carStartIm) = (_c3r, _c3i);
        (_blockCar, _blockModSq, _blockModN) = (0, 0, 0);
        var done = _blockMod[_blockNext];
        for (int i = 0; i < ModBins; i++) _mod[i] += done[i];
    }

    private void CloseBlock()
    {
        float mean = (float)(_blockSum / _blockFill);
        double var = Math.Max(0, _blockSumSq / _blockFill - (double)mean * mean);
        var b = new Block { Mean = mean, Max = _blockMax, Min = _blockMin, AcRms = (float)Math.Sqrt(var) };
        _blocks[_blockNext] = b;

        if (_setup.Kind == SignalKind.Am)
        {
            CloseCarrierBlock(ref b);
            _blocks[_blockNext] = b;
        }

        var done = _blockHist[_blockNext];
        for (int i = 0; i < HistBins; i++) _hist[i] += done[i];
        _rawPeaks[_blockNext] = _rawPeak;
        _blanked[_blockNext] = _blankBlock;
        _blankBlock = 0;
        _rawPeak = 0;
        _blockNext = (_blockNext + 1) % Blocks;
        if (_blockCount < Blocks) _blockCount++;
        // The block about to be reused leaves the totals.
        var old = _blockHist[_blockNext];
        for (int i = 0; i < HistBins; i++) { _hist[i] -= old[i]; old[i] = 0; }
        var oldMod = _blockMod[_blockNext];
        for (int i = 0; i < ModBins; i++) { _mod[i] -= oldMod[i]; oldMod[i] = 0; }
        (_blockFill, _blockSum, _blockSumSq, _blockMax, _blockMin, _blockWide) = (0, 0, 0, 0, float.MaxValue, 0);
    }

    // ---- spectrum -----------------------------------------------------------

    private int BinOf(double hz) => Math.Clamp((int)Math.Round(hz / (_fs / FftSize)) + FftSize / 2, 0, FftSize - 1);

    private void SpectrumFrame()
    {
        for (int i = 0; i < FftSize; i++)
        {
            _winRe[i] = _fftRe[i] * _window[i];
            _winIm[i] = _fftIm[i] * _window[i];
        }
        Fft(_winRe, _winIm);
        double scale = 1 / (_windowSum * _windowSum);
        for (int i = 0; i < FftSize; i++)
        {
            int src = (i + FftSize / 2) % FftSize;        // lowest frequency first
            _frame[i] = ((double)_winRe[src] * _winRe[src] + (double)_winIm[src] * _winIm[src]) * scale;
        }
        var (lo, hi) = Channel();
        double inChannel = 0;
        for (int i = BinOf(lo); i <= BinOf(hi); i++) inChannel += _frame[i];

        // Average only while the signal is there, so pauses in speech don't thin it out, and not
        // through a crash of static (wideband, so everything outside the channel jumps at once),
        // which would pass for a noise floor. Our own signal has no static, and its splatter is wanted.
        double total = 0;
        for (int i = 0; i < FftSize; i++) total += _frame[i];
        double outside = Math.Max(0, total - inChannel);
        bool crash = !_setup.Transmit && _outsideUsual > 0 && outside > _outsideUsual * 4;
        _outsideUsual = _outsideUsual <= 0 ? outside : _outsideUsual + (Math.Min(outside, _outsideUsual * 4) - _outsideUsual) * 0.05;
        _channelPeak = Math.Max(inChannel, _channelPeak * 0.995);
        bool present = !crash && inChannel >= _channelPeak * 0.003;
        for (int i = 0; i < FftSize; i++)
        {
            if (present) _avg[i] = _haveAvg ? _avg[i] + (_frame[i] - _avg[i]) * 0.08 : _frame[i];
            _peak[i] = Math.Max(_frame[i], _peak[i] * 0.985);
        }
        if (present) _haveAvg = true;
        if (present && _setup.Kind == SignalKind.Am) FindCarrier();
    }

    /// <summary>Where the carrier is, to within the loop's reach: the one narrow line near the dial that
    /// stands well clear of everything round it. The loop in <see cref="CloseCarrierBlock"/> does the rest.</summary>
    private void FindCarrier()
    {
        int b0 = BinOf(-1500), b1 = BinOf(1500), best = b0;
        var near = new double[b1 - b0 + 1];
        for (int i = b0; i <= b1; i++) { near[i - b0] = _avg[i]; if (_avg[i] > _avg[best]) best = i; }
        Array.Sort(near);
        bool found = _avg[best] > near[near.Length / 2] * 8;
        if (!found)
        {
            if (_carrierFound) { _carrierFound = false; _carSettle = 0; }
            return;
        }
        double a = Db(_avg[Math.Max(0, best - 1)]), b = Db(_avg[best]), c = Db(_avg[Math.Min(FftSize - 1, best + 1)]);
        double frac = Math.Abs(a - 2 * b + c) > 1e-6 ? 0.5 * (a - c) / (a - 2 * b + c) : 0;
        double hz = (best - FftSize / 2 + Math.Clamp(frac, -0.5, 0.5)) * (_fs / FftSize);
        if (_carrierFound && Math.Abs(hz - _carrierHz) < 8) return;
        _carrierFound = true;
        _carSettle = 0;
        SetCarrier(hz);
    }

    private void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len / 2, step = n / len;
            for (int i = 0; i < n; i += len)
                for (int k = 0; k < half; k++)
                {
                    float c = _cos[k * step], s = -_sin[k * step];
                    int a = i + k, b = a + half;
                    float tr = re[b] * c - im[b] * s, ti = re[b] * s + im[b] * c;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
    }

    // ---- out ----------------------------------------------------------------

    /// <summary>The picture now. <paramref name="columns"/> is the scope's width; the readings are
    /// only worked out when <paramref name="withFindings"/> is set.</summary>
    public SignalReport Snapshot(double scopeSeconds, int columns, bool withFindings)
    {
        lock (_lock)
        {
            var r = new SignalReport { Setup = _setup, ScopeSeconds = scopeSeconds };
            long have = Math.Min(_envCount, _env.Length);
            int n = (int)Math.Min(have, (long)(scopeSeconds * _fs));
            int total = Math.Max(1, (int)(scopeSeconds * _fs));
            columns = Math.Clamp(columns, 16, Math.Max(16, total));
            r.EnvMin = new float[columns];
            r.EnvMax = new float[columns];
            // The newest sample sits at the right edge; a short history leaves the left empty.
            long first = _envCount - n;
            for (int c = 0; c < columns; c++)
            {
                long a = _envCount - total + (long)c * total / columns, b = _envCount - total + (long)(c + 1) * total / columns;
                float lo = float.MaxValue, hi = 0;
                for (long i = Math.Max(a, first); i < Math.Max(b, a + 1) && i < _envCount; i++)
                {
                    if (i < first) continue;
                    float e = _env[(int)(i % _env.Length)];
                    if (e < lo) lo = e;
                    if (e > hi) hi = e;
                }
                (r.EnvMin[c], r.EnvMax[c]) = lo == float.MaxValue ? (0, 0) : (lo, hi);
            }

            var m = Measure();
            (r.ModulationUp, r.ModulationDown, r.ModulationAverage, r.ModulationNoise) = (m.ModPos, m.ModNeg, m.ModAverage, m.ModNoise);
            (r.HasDepth, r.SnrDb, r.CarrierOffsetHz) = (m.HaveDepth && m.HaveCarrier, m.SnrDb, m.CarrierOffsetHz);
            r.ImpulsesPerSecond = m.ImpulsesPerSecond;
            r.Peak = m.Peak;
            r.Carrier = _setup.Kind == SignalKind.Am ? m.Carrier : 0;

            double binHz = _fs / FftSize;
            int b0 = BinOf(-10000), b1 = BinOf(10000);
            r.SpectrumLowHz = (b0 - FftSize / 2) * binHz;
            r.BinHz = binHz;
            r.SpectrumDb = new float[b1 - b0 + 1];
            r.SpectrumPeakDb = new float[b1 - b0 + 1];
            for (int i = b0; i <= b1; i++)
            {
                r.SpectrumDb[i - b0] = Db(_avg[i]);
                r.SpectrumPeakDb[i - b0] = Db(_peak[i]);
            }
            (r.ChannelLowHz, r.ChannelHighHz) = Channel();
            if (withFindings) (r.Findings, r.Overall, r.Headline) = Judge(m);
            return r;
        }
    }

    private static float Db(double power) => power <= 1e-20 ? -200 : (float)(10 * Math.Log10(power));

    /// <summary>The numbers behind the readings.</summary>
    private struct Measured
    {
        public double Seconds, Peak, PeakDb, AverageDb, CrestDb, FlatTop, RawPeak, ImpulsesPerSecond;
        public double Carrier, ModPos, ModNeg, ModAverage, CarrierShift, ModNoise, ModCutOff;
        public bool HaveShift, HaveCarrier, HaveDepth;
        public double SnrDb, NoiseLimitDbc, SplatterDbc, CarrierOffsetHz;
        public bool SplatterAtNoise, SplatterOpposite, HaveSpectrum;
        public double EdgeLowHz, EdgeHighHz, TiltDb;
        public bool EdgesLimited;
    }

    private Measured Measure()
    {
        var m = new Measured { Seconds = _blockCount * _blockLen / _fs };
        foreach (float p in _rawPeaks) if (p > m.RawPeak) m.RawPeak = p;
        m.RawPeak = Math.Max(m.RawPeak, _rawPeak);
        int impulses = 0;
        foreach (int c in _blanked) impulses += c;
        if (m.Seconds > 0) m.ImpulsesPerSecond = impulses / m.Seconds;

        // Envelope: the peak (all but the top twentieth of a percent), the average while
        // there is signal, and how much of that time sits right at the peak.
        long total = 0;
        foreach (int c in _hist) total += c;
        if (total > 0)
        {
            long want = Math.Max(1, total / 2000), seen = 0;
            int peakBin = HistBins - 1;
            for (; peakBin > 0; peakBin--) { seen += _hist[peakBin]; if (seen >= want) break; }
            m.PeakDb = HistFloorDb + (peakBin + 0.5) * HistStepDb;
            m.Peak = Math.Pow(10, m.PeakDb / 20);
            int floorBin = Math.Max(0, peakBin - (int)(30 / HistStepDb));
            long active = 0, top = 0;
            double power = 0;
            for (int i = floorBin; i < HistBins; i++)
            {
                active += _hist[i];
                power += _hist[i] * Math.Pow(10, (HistFloorDb + (i + 0.5) * HistStepDb) / 10);
                if (i >= peakBin - 3) top += _hist[i];
            }
            if (active > 0)
            {
                m.AverageDb = 10 * Math.Log10(power / active);
                m.CrestDb = m.PeakDb - m.AverageDb;
                m.FlatTop = (double)top / active;
            }
        }

        // AM: the carrier and the depth over the blocks where it was steady. The peaks are the
        // levels passed for about half a millisecond in the five seconds, not one stray sample.
        int valid = 0, loud = 0;
        double carrier = 0, mean = 0;
        for (int i = 1; i <= _blockCount; i++)
        {
            var b = _blocks[(_blockNext - i + Blocks) % Blocks];
            mean += b.Mean;
            if (!b.Valid) continue;
            carrier += b.Carrier;
            valid++;
            if (b.ModRms < 0.03) continue;
            m.ModAverage += (double)b.ModRms * b.ModRms;
            loud++;
        }
        m.HaveCarrier = _carrierFound;
        m.Carrier = valid > 0 ? carrier / valid : _blockCount > 0 ? mean / _blockCount : 0;
        if (loud > 0) m.ModAverage = Math.Sqrt(m.ModAverage / loud);
        long depthTotal = 0;
        foreach (int c in _mod) depthTotal += c;
        if (valid >= 10 && depthTotal > 0)
        {
            m.HaveDepth = true;
            long tail = Math.Max(1, (long)(depthTotal / DepthTail)), seen = 0;
            int hi = ModBins - 1, lo = 0;
            for (; hi > 0; hi--) { seen += _mod[hi]; if (seen >= tail) break; }
            for (seen = 0; lo < ModBins - 1; lo++) { seen += _mod[lo]; if (seen >= tail) break; }
            // How much of the time the carrier is all but gone: a clipped modulator sits there, a clean one only touches.
            long gone = 0;
            for (int i = 0; ModFloor + (i + 1) * ModStep <= -0.97; i++) gone += _mod[i];
            m.ModCutOff = (double)gone / depthTotal;
            m.ModPos = Math.Max(0, ModFloor + (hi + 0.5) * ModStep);
            m.ModNeg = Math.Max(0, -(ModFloor + (lo + 0.5) * ModStep));
        }
        if (_haveQuiet && _haveLoud && _carrierQuiet > 0)
        {
            m.HaveShift = true;
            m.CarrierShift = _carrierLoud / _carrierQuiet - 1;
        }

        if (_haveAvg) MeasureSpectrum(ref m);
        return m;
    }

    private void MeasureSpectrum(ref Measured m)
    {
        m.HaveSpectrum = true;
        double binHz = _fs / FftSize;
        var (lo, hi) = Channel();
        int c0 = BinOf(lo), c1 = BinOf(hi);

        // Noise: the quiet fifth of what lies outside the channel, within 10 kHz.
        var outside = new List<double>();
        int w0 = BinOf(-10000), w1 = BinOf(10000);
        for (int i = w0; i <= w1; i++)
            if (i < BinOf(lo - GuardHz) || i > BinOf(hi + GuardHz)) outside.Add(_avg[i]);
        outside.Sort();
        double noise = outside.Count > 0 ? outside[outside.Count / 5] : 0;

        double inChannel = 0;
        for (int i = c0; i <= c1; i++) inChannel += _avg[i];
        int nIn = c1 - c0 + 1;
        double signal = inChannel - noise * nIn;
        m.SnrDb = signal > 0 && noise > 0 ? 10 * Math.Log10(signal / (noise * nIn)) : signal > 0 ? 99 : -99;
        if (signal <= 0) return;

        // Energy outside the channel: above it, and below it (the opposite sideband in SSB).
        (double Sum, int N) Region(double a, double b)
        {
            double s = 0;
            int i0 = BinOf(Math.Min(a, b)), i1 = BinOf(Math.Max(a, b));
            for (int i = i0; i <= i1; i++) s += _avg[i];
            return (s, i1 - i0 + 1);
        }
        var above = Region(hi + GuardHz, hi + GuardHz + AdjacentHz);
        var below = Region(lo - GuardHz, lo - GuardHz - AdjacentHz);
        bool lower = _setup.Kind == SignalKind.Ssb && _setup.LowerSideband;
        bool worstBelow = below.Sum - noise * below.N > above.Sum - noise * above.N;
        var worst = worstBelow ? below : above;
        // "Opposite" is the side the carrier is on: below the channel in USB, above it in LSB.
        m.SplatterOpposite = _setup.Kind == SignalKind.Ssb && worstBelow != lower;
        double excess = worst.Sum - noise * worst.N;
        m.NoiseLimitDbc = noise > 0 ? 10 * Math.Log10(noise * worst.N / signal) : -120;
        m.SplatterAtNoise = excess < noise * worst.N;
        m.SplatterDbc = excess > 0 ? 10 * Math.Log10(excess / signal) : -120;

        // AM: where the carrier really is, and what the noise inside the depth filter adds. Noise in
        // phase with the carrier reads as modulation: its share is taken out of the peaks and the average.
        if (_setup.Kind == SignalKind.Am)
        {
            if (_carrierFound) m.CarrierOffsetHz = _carrierHz;
            if (m.Carrier > 0)
            {
                double inPhase = Math.Sqrt(noise * 2 * _envHalf / (1.5 * binHz) / 2) / m.Carrier;
                m.ModNoise = DepthTailSigma * inPhase;           // what noise alone would read as a peak
                m.ModPos = LessNoise(m.ModPos, m.ModNoise);
                m.ModNeg = LessNoise(m.ModNeg, m.ModNoise);
                m.ModAverage = Math.Sqrt(Math.Max(0, m.ModAverage * m.ModAverage - inPhase * inPhase)) * Math.Sqrt(2);
            }
        }

        // Edges: where the smoothed spectrum falls 26 dB below its highest point in the channel.
        const int Smooth = 4;
        double At(int i)
        {
            double s = 0;
            int n = 0;
            for (int k = Math.Max(0, i - Smooth); k <= Math.Min(FftSize - 1, i + Smooth); k++) { s += _avg[k]; n++; }
            return s / n;
        }
        int top = c0;
        double topValue = 0;
        int carrierLo = BinOf(m.CarrierOffsetHz - 80), carrierHi = BinOf(m.CarrierOffsetHz + 80);
        for (int i = c0; i <= c1; i++)
        {
            if (_setup.Kind == SignalKind.Am && i >= carrierLo - Smooth && i <= carrierHi + Smooth) continue;   // the sidebands, not the carrier
            double v = At(i);
            if (v > topValue) (topValue, top) = (v, i);
        }
        double threshold = Math.Max(topValue / 400, noise * 4);
        m.EdgesLimited = noise * 4 > topValue / 400;
        int run = (int)(300 / binHz);
        int start = _setup.Kind == SignalKind.Am ? BinOf(m.CarrierOffsetHz) : top;
        int Walk(int dir)
        {
            int edge = top, quiet = 0;
            for (int i = start; i >= w0 && i <= w1; i += dir)
            {
                if (At(i) >= threshold) { edge = i; quiet = 0; }
                else if (++quiet > run) break;
            }
            return edge;
        }
        m.EdgeLowHz = (Walk(-1) - FftSize / 2) * binHz;
        m.EdgeHighHz = (Walk(1) - FftSize / 2) * binHz;

        // SSB: the highs (1200-3000 Hz) against the lows (150-600 Hz).
        if (_setup.Kind == SignalKind.Ssb)
        {
            double sign = lower ? -1 : 1;
            var lows = Region(sign * 150, sign * 600);
            var highs = Region(sign * 1200, sign * Math.Min(3000, High));
            double l = lows.Sum - noise * lows.N, h = highs.Sum - noise * highs.N;
            m.TiltDb = l > 0 && h > 0 ? 10 * Math.Log10(h / l) : h > 0 ? 30 : -30;
        }
    }

    /// <summary>A peak reading with the noise's share taken out. Noise and modulation don't simply add:
    /// with no modulation the reading is all noise, and on full modulation noise adds a part of what
    /// it would read alone (most on a steady tone, less on speech). This sits between the two.</summary>
    private static double LessNoise(double reading, double noise)
    {
        if (noise <= 0) return reading;
        double rest = Math.Sqrt(Math.Max(0, reading * reading - noise * noise));
        return Math.Max(0, rest - 0.35 * noise * Math.Clamp((reading - noise) / noise, 0, 1));
    }

    // ---- what it means ------------------------------------------------------

    private static string F(double v, string format = "0") => v.ToString(format, CultureInfo.CurrentCulture);

    private (IReadOnlyList<Finding>, FindingLevel, string) Judge(Measured m)
    {
        var s = _setup;
        var list = new List<Finding>();
        bool tx = s.Transmit;
        string who = tx ? "Your signal" : "This signal";

        if (_inRate == 0 || m.Seconds < 0.5)
            return (list, FindingLevel.None, tx ? "Listening to your transmission…" : "Listening…");

        // The receiver itself comes first: an overloaded receiver distorts everything it shows.
        double rawDb = m.RawPeak > 0 ? 20 * Math.Log10(m.RawPeak) : -200;
        if (m.RawPeak >= 0.9)
            list.Add(new(FindingLevel.Bad, "Receiver", $"{F(rawDb)} dBFS: overloaded",
                tx ? "The receiver is overloaded by your own transmission, so nothing below can be trusted. Add receive attenuation while transmitting (RFG), or lower the power for this check."
                   : "The receiver is overloaded, so what it shows is its own distortion. Add attenuation (RFG) and look again."));

        if (m.ImpulsesPerSecond >= 0.5)
            list.Add(new(FindingLevel.Info, "Impulses", $"{F(m.ImpulsesPerSecond, m.ImpulsesPerSecond < 10 ? "0.0" : "0")} a second, removed",
                tx ? "Very short spikes, far stronger than your signal, are reaching the receiver while you transmit. They are left out of the readings. They are not part of your modulation; if they are there into a dummy load too, they come from inside the station and not from the band."
                   : "Very short spikes (static, or electrical noise nearby) are being left out of the readings, as a noise blanker would."));

        bool present = m.HaveSpectrum && m.SnrDb >= 10;
        if (!present)
        {
            string wait = tx
                ? s.Kind == SignalKind.Am ? "Transmitting, but no carrier is reaching the receiver." : "Transmitting: talk to see your signal."
                : "No signal on this frequency.";
            list.Add(new(FindingLevel.None, "Signal", m.HaveSpectrum && m.SnrDb > -50 ? $"{F(Math.Max(0, m.SnrDb))} dB over the noise" : "none",
                tx ? "This shows your signal as your own receiver hears it while you transmit. Key up and talk normally for a few seconds."
                   : "Tune a signal onto the dial. The readings need it about 20 dB above the noise."));
            return (list, Worst(list), wait);
        }

        bool weak = m.SnrDb < 20;
        list.Add(new(weak ? FindingLevel.Caution : FindingLevel.Good, "Signal",
            tx ? $"{F(m.PeakDb)} dBFS at the receiver" : $"{F(Math.Min(m.SnrDb, 99))} dB over the noise",
            weak ? "Too weak for a fair judgement: noise and fading look like faults at this level. Treat the readings below as rough."
                 : tx ? "Your own signal, picked up by the receiver while you transmit: a good level to measure."
                      : "Strong enough to measure. Fading and interference can still upset the readings on a received signal."));

        if (s.Kind == SignalKind.Other)
        {
            Bandwidth(list, m, s, tx);
            Splatter(list, m, s, tx, false);
            list.Add(new(FindingLevel.None, "Mode", "not SSB or AM", "The scopes work in every mode, but the advice is for SSB and AM voice."));
            return (list, Worst(list), "Scopes only in this mode");
        }

        bool tone = s.TestTone || m.CrestDb < 2.0 && s.Kind == SignalKind.Ssb;
        bool splatter = Splatter(null, m, s, tx, false);

        if (s.Kind == SignalKind.Ssb)
        {
            if (tone)
            {
                list.Add(new(FindingLevel.None, "Peaks", s.TestTone ? "test tone" : "steady tone",
                    "A steady tone or carrier, not speech: the peak and density readings don't apply. The spectrum and the splatter reading still do."));
            }
            else
            {
                // Peaks held at one level. With a clean spectrum that is a limiter doing its job;
                // with energy outside the channel it is clipping.
                string pct = m.FlatTop >= 0.1 ? F(m.FlatTop * 100) : F(m.FlatTop * 100, "0.0");
                string alc = tx && !double.IsNaN(s.AlcDb) && s.AlcDb >= 1 ? $" ALC is taking off {F(s.AlcDb)} dB." : "";
                string mic = tx && !double.IsNaN(s.MicPeakDbfs) && s.MicPeakDbfs > -1.5 ? " The microphone input is reaching full scale: lower the mic gain at its source." : "";
                if (m.FlatTop < 0.03)
                    list.Add(new(FindingLevel.Good, "Peaks", "rounded", "The peaks of your speech keep their shape: nothing is clipping." + mic));
                else if (!splatter)
                    list.Add(new(m.FlatTop < 0.15 ? FindingLevel.Good : FindingLevel.Info, "Peaks", $"held level {pct}% of the time",
                        (tx ? "The peaks are being held at one level, which is what ALC and a speech processor do, and the spectrum is staying clean." + alc
                            : "The peaks are held at one level by the other station's processing, and the signal is staying clean.")
                        + (m.FlatTop >= 0.15 ? " This is heavy: it gains little more loudness and starts to sound harsh." : "") + mic));
                else
                    list.Add(new(FindingLevel.Bad, "Peaks", $"flat-topping {pct}% of the time",
                        (tx ? "The peaks are being cut off and the signal is spreading outside its channel. Likely causes: too much drive for the amplifier, an amplifier pushed past its rated power, or mic gain and processing set too high." + alc + " Lower the drive first; if it stays, lower the mic gain or processing."
                            : "The peaks are cut off and the signal spreads outside its channel: the other station is overdriving its transmitter or amplifier.") + mic));

                string crest = $"peak {F(m.CrestDb)} dB over average";
                if (m.CrestDb > 13)
                    list.Add(new(FindingLevel.Info, "Density", crest,
                        tx ? "Little average power for the peak power: you will sound quieter than the meter suggests. A little compression, the leveler or CFC would raise it."
                           : "Unprocessed audio: low average power for its peak power."));
                else if (m.CrestDb >= 6)
                    list.Add(new(FindingLevel.Good, "Density", crest, "A good balance of loudness and natural sound."));
                else
                    list.Add(new(splatter ? FindingLevel.Bad : FindingLevel.Caution, "Density", crest,
                        tx ? "Very dense: heavy compression or clipping. It is loud but tiring to listen to, and background noise comes up between words. Ease off the processing."
                           : "Very dense: heavily compressed or clipped audio."));
            }
            if (!tone)
            {
                Bandwidth(list, m, s, tx);
                string tilt = m.TiltDb >= 0 ? $"highs {F(m.TiltDb)} dB above the lows" : $"highs {F(-m.TiltDb)} dB below the lows";
                if (m.TiltDb < -18)
                    list.Add(new(FindingLevel.Caution, "Tone", tilt,
                        tx ? "Bass-heavy: most of the power is below 600 Hz, where it adds warmth but not readability. Raise the highs or cut the lows in the TX equalizer, or raise the low cut."
                           : "Bass-heavy audio: muffled and harder to copy in noise."));
                else if (m.TiltDb > 4)
                    list.Add(new(FindingLevel.Info, "Tone", tilt,
                        tx ? "Thin: little low end. That cuts through a pile-up, but sounds harsh in a relaxed contact; add some lows in the TX equalizer if you want it fuller."
                           : "Thin audio with little low end: a contest or DX setting."));
                else
                    list.Add(new(FindingLevel.Good, "Tone", tilt, "Balanced: enough highs to be readable, enough lows to sound natural."));
            }
        }
        else
        {
            double pos = m.ModPos * 100, neg = m.ModNeg * 100;
            string depth = $"{(m.ModNoise > 0.08 ? "about " : "")}+{F(pos)}% / −{F(neg)}%";
            if (!m.HaveCarrier)
            {
                pos = neg = 0;
                list.Add(new(FindingLevel.None, "Modulation", "no carrier on the dial",
                    tx ? "No steady carrier is reaching the receiver, so the depth can't be measured."
                       : "There is no carrier within 1.5 kHz of the dial, so the depth can't be measured. Tune to the station's carrier (the tall line in the spectrum)."));
            }
            else if (!m.HaveDepth)
            {
                pos = neg = 0;
                list.Add(new(FindingLevel.None, "Modulation", "carrier not steady",
                    "The carrier is fading or has only just appeared: the depth is measured while it holds still."));
            }
            else if (m.ModNoise > 0.3)
            {
                pos = neg = 0;
                list.Add(new(FindingLevel.None, "Modulation", "too noisy to measure",
                    $"Noise alone would read about {F(m.ModNoise * 100)}% on a carrier this weak. A narrower RX filter lowers that; a stronger signal lowers it more."));
            }
            else if (Math.Max(pos, neg) < Math.Max(5, m.ModNoise * 60))
                list.Add(new(FindingLevel.None, "Modulation", "carrier only", tx ? "The carrier is up but there is no audio on it yet: talk." : "A carrier with no audio on it."));
            else if (neg > 105 || neg >= 97 && m.ModCutOff > 0.003 && m.ModNoise < 0.1)
                list.Add(new(tx ? FindingLevel.Bad : FindingLevel.Caution, "Modulation", depth,
                    tx ? "Over-modulating: the downward peaks reach or pass 100%, where the carrier is cut off. That distorts the audio and splatters across the band. Lower the mic gain or processing until the downward peaks stay under 100%."
                       : "The downward peaks reach or pass 100%: the carrier is gone at those moments. That is over-modulation, or selective fading taking the carrier down, which sounds the same."));
            else if (neg >= 98)
                list.Add(new(tx ? FindingLevel.Caution : FindingLevel.Good, "Modulation", depth,
                    tx ? "Right at the limit: the downward peaks are touching 100%. A little less mic gain or processing gives a margin; past 100% it splatters."
                       : "Fully modulated, with the downward peaks right at 100%, as a processed broadcast signal is."
                         + (pos > 110 ? " The upward peaks go higher, which is deliberate and harmless." : "")));
            else if (Math.Max(pos, neg) < 40)
                list.Add(new(FindingLevel.Caution, "Modulation", depth,
                    tx ? "Low modulation: you will sound quiet however strong the carrier is. Raise the mic gain or add processing; aim for peaks of 80 to 100%."
                       : "Lightly modulated: quiet audio for its signal strength."));
            else
                list.Add(new(FindingLevel.Good, "Modulation", depth,
                    (neg >= 90 ? "Full modulation with the downward peaks still under 100%." : "Clean modulation with room to spare.")
                    + (pos > 110 ? " The upward peaks go past 100%, which is fine (voices are lopsided) as long as the downward ones don't." : "")));

            if (Math.Max(pos, neg) >= 5)
            {
                // For speech, 40 to 55% is loud and still clean; more than that takes heavy processing.
                string avg = $"{F(m.ModAverage * 100)}% average";
                double loud = m.ModAverage * 100;
                if (s.TestTone)
                    list.Add(new(FindingLevel.None, "Loudness", avg, "A test tone: the 40 to 55% target is for speech."));
                else if (loud < 20)
                    list.Add(new(FindingLevel.Info, "Loudness", avg + " (target 40 to 55%)",
                        tx ? "Quiet for its peaks: unprocessed, peaky audio. The leveler or some compression would make you much louder at the same peak level."
                           : "Peaky, unprocessed audio: quiet for its signal strength."));
                else if (loud < 40)
                    list.Add(new(FindingLevel.Info, "Loudness", avg + " (target 40 to 55%)",
                        tx ? "Natural, but a little quiet. A little more leveler or compression would bring it into the 40 to 55% that sounds loud and still clean."
                           : "Lightly processed audio: natural, a little quiet."));
                else if (loud <= 55)
                    list.Add(new(FindingLevel.Good, "Loudness", avg + " (on target)", "In the 40 to 55% that is loud without sounding squashed."));
                else if (loud <= 65)
                    list.Add(new(FindingLevel.Info, "Loudness", avg + " (target 40 to 55%)",
                        tx ? "Louder than the target: dense processing. Fine while the downward peaks and the splatter reading stay good, but every 10 points is under 2 dB to the listener."
                           : "Densely processed audio: loud."));
                else
                    list.Add(new(FindingLevel.Caution, "Loudness", avg + " (target 40 to 55%)",
                        tx ? "Very dense: heavy compression or clipping. It is tiring to listen to and brings background noise up between words, for little more loudness. Ease off the processing."
                           : "Very dense audio: heavily compressed or clipped."));
            }

            if (tx && m.HaveShift)
            {
                double shift = m.CarrierShift * 100;
                list.Add(Math.Abs(shift) > 5
                    ? new(FindingLevel.Caution, "Carrier", $"{(shift < 0 ? "drops" : "rises")} {F(Math.Abs(shift))}% with audio",
                        shift < 0 ? "The carrier sags when you talk: the power supply or amplifier is running out of headroom, or ALC is pulling it down. In AM the carrier should sit at a quarter of the peak power: lower the drive."
                                  : "The carrier rises when you talk, which means the audio isn't symmetrical after processing. Usually harmless.")
                    : new(FindingLevel.Good, "Carrier", "steady", "The carrier holds its level while you talk."));
            }
            else if (!tx && Math.Abs(m.CarrierOffsetHz) >= 20)
                list.Add(new(FindingLevel.Info, "Carrier", $"{F(Math.Abs(m.CarrierOffsetHz))} Hz {(m.CarrierOffsetHz > 0 ? "above" : "below")} the dial",
                    "The station's carrier isn't where you are tuned. SAM follows it; in plain AM, tune to it for the cleanest audio."));
            if (Math.Max(pos, neg) >= 5) Bandwidth(list, m, s, tx);
        }

        Splatter(list, m, s, tx, weak);
        var worst = Worst(list);
        string headline = worst switch
        {
            FindingLevel.Bad => $"{who} has a problem",
            FindingLevel.Caution => weak ? "Too weak to judge well" : $"{who} is worth a look",
            _ => tone && s.Kind == SignalKind.Ssb ? "Steady tone" : $"{who} looks good",
        };
        return (list, worst, headline);
    }

    private static FindingLevel Worst(List<Finding> list)
    {
        var worst = FindingLevel.None;
        foreach (var f in list) if (f.Level > worst) worst = f.Level;
        return worst == FindingLevel.Info ? FindingLevel.Good : worst;
    }

    private void Bandwidth(List<Finding> list, Measured m, SignalSetup s, bool tx)
    {
        double lo = m.EdgeLowHz, hi = m.EdgeHighHz, width = hi - lo;
        string atLeast = m.EdgesLimited ? "at least " : "";
        if (s.Kind == SignalKind.Ssb)
        {
            double a = s.LowerSideband ? -hi : lo, b = s.LowerSideband ? -lo : hi;      // audio frequencies
            string value = $"{atLeast}{F(Math.Round(Math.Max(0, a) / 10) * 10)} to {F(Math.Round(b / 10) * 10)} Hz";
            if (b > High + 400 || a < -400)
                list.Add(new(FindingLevel.Caution, "Bandwidth", value,
                    tx ? $"Wider than your TX filter ({High} Hz): the extra is distortion, not audio. See the splatter reading."
                       : $"Wider than a {F(High / 1000.0, "0.0")} kHz channel: either wide audio or distortion products."));
            else if (width > 3600 && !tx)
                list.Add(new(FindingLevel.Info, "Bandwidth", value, "A wide (extended) SSB signal: more than the usual 2.4 to 3 kHz."));
            else if (width < 1800)
                list.Add(new(FindingLevel.Info, "Bandwidth", value,
                    tx ? "Narrow audio: it concentrates power, but voices lose character below about 2 kHz of width. Check the TX filter."
                       : "Narrow audio."));
            else
                list.Add(new(FindingLevel.Good, "Bandwidth", value,
                    tx ? "The audio your signal carries, inside your TX filter." : "The audio range this signal carries."));
        }
        else
        {
            string value = $"{atLeast}{F(width / 1000, "0.0")} kHz wide";
            bool over = Math.Max(-lo, hi) > High + 500;
            list.Add(over
                ? new(FindingLevel.Caution, "Bandwidth", value,
                    tx ? $"Wider than twice your TX filter's {High} Hz: the extra is distortion. See the splatter reading."
                       : "Wider than the channel it is being judged against: wide audio or distortion products.")
                : new(FindingLevel.Good, "Bandwidth", value,
                    s.Kind == SignalKind.Am ? "Both sidebands together: twice the highest audio frequency." : "The width that holds nearly all of the signal's power."));
        }
    }

    /// <summary>Adds the reading; true when there is clearly energy outside the channel.</summary>
    private bool Splatter(List<Finding>? list, Measured m, SignalSetup s, bool tx, bool weak)
    {
        if (!m.HaveSpectrum) return false;
        string side = s.Kind != SignalKind.Ssb ? "beside the channel" : m.SplatterOpposite ? "on the opposite sideband" : "above the passband";
        if (m.SplatterAtNoise)
        {
            list?.Add(m.NoiseLimitDbc <= -30
                ? new(FindingLevel.Good, "Splatter", $"below {F(m.NoiseLimitDbc)} dB", "Nothing measurable outside the channel: a clean signal.")
                : new(FindingLevel.None, "Splatter", "can't tell", "The signal isn't far enough above the noise to show what it puts outside its channel."));
            return false;
        }
        double d = m.SplatterDbc;
        string value = $"{F(d)} dB {side}";
        if (d <= -35)
        {
            list?.Add(new(FindingLevel.Good, "Splatter", value, "Very little energy outside the channel: a clean signal."));
            return false;
        }
        bool bad = d > -25;
        list?.Add(new(weak ? FindingLevel.Caution : bad ? FindingLevel.Bad : FindingLevel.Caution, "Splatter", value,
            tx ? (bad ? "A lot of your power is landing outside your channel, where it interferes with others. "
                      : "More energy outside your channel than a clean transmitter gives. ")
                 + "It comes from the amplifier chain being driven too hard, or from clipped audio. Lower the drive, then the processing, and watch this fall; PureSignal lowers it further."
               : "Energy outside this station's channel. If it comes and goes with the voice it is the station's splatter; if it is steady it is a neighbouring signal."));
        return true;
    }
}
