using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SimpleHPSDR.DeepCw;

/// <summary>
/// Streaming CW decoder using the DeepCW neural network (e04, MIT; see DeepCw/README.md).
/// A C# port of deepcw-engine's examples/python/decode_morse.py: 48 kHz receive audio
/// in, decoded text out about 2-4 s later. Every second it runs the model over up to
/// 12 s of audio and stitches the overlapping windows together, committing text once
/// it is 2 s behind the newest audio.
///
/// <see cref="Push"/> may be called from any thread; decoding runs on its own task and
/// <see cref="Decoded"/> is raised there.
/// </summary>
public sealed class DeepCwDecoder : IDisposable
{
    // From the reference decoder.
    private const double NoiseLevelMinRelativeToPeak = 1e-3;
    private const double NoiseLevelMin = 1e-5;
    private const double MaxSegmentSeconds = 12.0;
    private const double MinPendingSeconds = 2.0;
    private const double StepSeconds = 1.0;
    private const double StitchLeftContextSeconds = 4.0;
    private const double StitchFreezeGuardSeconds = 2.0;
    private const double StitchLeftRampSeconds = 2.0;
    private const double StitchRightRampSeconds = 1.0;
    private const int StitchDilationFrames = 1;
    private const int WordBoundaryBlankHops = 134;
    private const float MinViewWeight = 1e-3f;
    private const float UncoveredLogProb = -30f;

    private const int InputRate = 48000;

    private readonly InferenceSession _session;
    private readonly Meta _meta;
    private readonly Decimator _decimator;
    private readonly Spectrogram _spectrogram;
    private readonly Stitcher _stitcher;
    private readonly object _audioLock = new();
    private float[] _audio = new float[1 << 16];   // model-rate audio, absolute index _audioBase + i
    private int _audioCount;
    private long _audioBase;
    private long _nextEndSample;
    private bool _hasTranscript;
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    /// <summary>Committed text, with any leading space already decided.</summary>
    public event Action<string>? Decoded;
    /// <summary>A decode error; the decoder stops.</summary>
    public event Action<string>? Failed;

    private DeepCwDecoder(InferenceSession session, Meta meta)
    {
        _session = session;
        _meta = meta;
        _decimator = new Decimator(InputRate / meta.SampleRate);
        _spectrogram = new Spectrogram(meta);
        _stitcher = new Stitcher(meta);
        _nextEndSample = (long)(MinPendingSeconds * meta.SampleRate);
        _worker = Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);
    }

    /// <summary>The model folder next to the app.</summary>
    public static string ModelDir => Path.Combine(AppContext.BaseDirectory, "DeepCw");

    /// <summary>Load the model. Null with a reason when it can't run here (no
    /// model files, or no ONNX Runtime for this platform, such as Intel Macs).</summary>
    public static DeepCwDecoder? TryCreate(out string error)
    {
        error = "";
        try
        {
            var meta = Meta.Load(Path.Combine(ModelDir, "model.onnx.json"));
            if (InputRate % meta.SampleRate != 0)
                throw new InvalidDataException($"model rate {meta.SampleRate} Hz doesn't divide 48 kHz");
            var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
            var session = new InferenceSession(Path.Combine(ModelDir, "model.onnx"), options);
            return new DeepCwDecoder(session, meta);
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException
                                       or BadImageFormatException)
        {
            error = "DeepCW isn't available on this computer (no ONNX Runtime for it).";
        }
        catch (Exception e)
        {
            error = $"DeepCW couldn't load: {e.Message}";
        }
        return null;
    }

    /// <summary>Is DeepCW usable here? Loads the model once to find out.</summary>
    public static bool IsAvailable(out string error)
    {
        using var d = TryCreate(out error);
        return d is not null;
    }

    /// <summary>48 kHz mono receive audio.</summary>
    public void Push(ReadOnlySpan<float> audio48k)
    {
        lock (_audioLock)
        {
            foreach (var x in audio48k)
            {
                if (!_decimator.Push(x, out float y)) continue;
                if (_audioCount == _audio.Length) Array.Resize(ref _audio, _audio.Length * 2);
                _audio[_audioCount++] = y;
            }
        }
        _wake.Set();
    }

    private void Run()
    {
        int stepSamples = (int)(StepSeconds * _meta.SampleRate);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                _wake.WaitOne(500);
                while (!_stop.IsCancellationRequested)
                {
                    float[] window;
                    long startFrame;
                    lock (_audioLock)
                    {
                        if (_audioBase + _audioCount < _nextEndSample) break;
                        startFrame = _stitcher.WindowStartFrame(_nextEndSample);
                        long startSample = startFrame * _stitcher.HopLength;
                        int from = (int)(startSample - _audioBase), to = (int)(_nextEndSample - _audioBase);
                        window = _audio.AsSpan(from, to - from).ToArray();
                    }
                    string text = RunWindow(window, startFrame);
                    lock (_audioLock)
                    {
                        _nextEndSample += stepSamples;
                        // Keep only what a later window can still start from.
                        long keepFrom = _stitcher.ContextStartFrame * _stitcher.HopLength;
                        int drop = (int)Math.Clamp(keepFrom - _audioBase, 0, _audioCount);
                        if (drop > 0)
                        {
                            Array.Copy(_audio, drop, _audio, 0, _audioCount - drop);
                            _audioCount -= drop;
                            _audioBase += drop;
                        }
                    }
                    if (text.Length > 0) Decoded?.Invoke(text);
                }
            }
        }
        catch (Exception e) when (!_stop.IsCancellationRequested)
        {
            Failed?.Invoke($"DeepCW stopped: {e.Message}");
        }
    }

    private string RunWindow(float[] audio, long startFrame)
    {
        if (audio.Length < _meta.FftLength) return "";
        var (spec, frames) = _spectrogram.Compute(audio);
        var input = new DenseTensor<float>(spec, [1, 1, frames, _meta.Bins]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_meta.InputName, input)]);
        var output = results.First(r => r.Name == _meta.OutputName).AsTensor<float>();
        int t = output.Dimensions[1], c = output.Dimensions[2];
        var logits = new float[t * c];
        int k = 0;
        foreach (var v in output) logits[k++] = v;   // [1, t, c] row-major
        _stitcher.AddWindow(startFrame, logits, t, isFinal: false);
        var committed = _stitcher.Take(final: false);
        if (committed is not { Text.Length: > 0 } lane) return "";
        string text = (lane.PrependSpace && _hasTranscript ? " " : "") + lane.Text;
        _hasTranscript = true;
        return text;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _wake.Set();
        try { _worker.Wait(2000); } catch (AggregateException) { }
        _session.Dispose();
        _wake.Dispose();
    }

    // ---- model metadata ----

    private sealed record Meta(string[] Chars, int BlankIndex, int SampleRate, int FftLength, int HopLength,
        int OutputTimeStride, double MinHz, double MaxHz, int Bins, double NoiseQuantile, int NumClasses,
        string InputName, string OutputName)
    {
        public static Meta Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            string norm = r.GetProperty("normalization").GetString() ?? "";
            if (!norm.StartsWith("noise_q") || !norm.EndsWith("_log1p"))
                throw new InvalidDataException($"unsupported normalization {norm}");
            return new Meta(
                [.. r.GetProperty("chars").EnumerateArray().Select(e => e.GetString() ?? "")],
                r.GetProperty("blank_index").GetInt32(),
                r.GetProperty("sample_rate").GetInt32(),
                r.GetProperty("fft_length").GetInt32(),
                r.GetProperty("hop_length").GetInt32(),
                r.TryGetProperty("output_time_stride", out var s) ? s.GetInt32() : 1,
                r.GetProperty("spectrogram_min_freq_hz").GetDouble(),
                r.GetProperty("spectrogram_max_freq_hz").GetDouble(),
                r.GetProperty("spectrogram_frequency_bins").GetInt32(),
                r.TryGetProperty("noise_level_quantile", out var q) ? q.GetDouble() : 0.9,
                r.GetProperty("num_classes").GetInt32(),
                r.GetProperty("onnx_input_name").GetString() ?? "spectrogram",
                r.GetProperty("onnx_output_name").GetString() ?? "log_probs");
        }
    }

    // ---- 48 kHz to model rate ----

    /// <summary>Windowed-sinc low-pass and keep every Nth sample. 48 kHz to 3.2 kHz:
    /// flat to about 1.2 kHz, the top of the model's band, and anything that could
    /// fold back below 1.2 kHz is well down.</summary>
    private sealed class Decimator
    {
        private readonly int _factor;
        private readonly float[] _taps;
        private readonly float[] _history;
        private int _pos, _phase;

        public Decimator(int factor)
        {
            _factor = factor;
            const int n = 481;
            double cutoff = 1450.0 / InputRate;   // cycles per sample
            _taps = new float[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double m = i - (n - 1) / 2.0;
                double sinc = m == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * m) / (Math.PI * m);
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (n - 1));
                _taps[i] = (float)(sinc * w);
                sum += _taps[i];
            }
            for (int i = 0; i < n; i++) _taps[i] /= (float)sum;
            _history = new float[n];
        }

        public bool Push(float x, out float y)
        {
            _history[_pos] = x;
            _pos = (_pos + 1) % _history.Length;
            y = 0;
            if (++_phase < _factor) return false;
            _phase = 0;
            double acc = 0;
            int p = _pos;   // oldest sample
            for (int i = 0; i < _taps.Length; i++)
            {
                acc += _taps[i] * _history[p];
                if (++p == _history.Length) p = 0;
            }
            y = (float)acc;
            return true;
        }
    }

    // ---- spectrogram ----

    private sealed class Spectrogram
    {
        private readonly int _fft, _hop, _bins;
        private readonly float[] _cos, _sin;   // [bin, n], Hann window folded in
        private readonly double _quantile, _rayleigh;

        public Spectrogram(Meta m)
        {
            (_fft, _hop, _bins, _quantile) = (m.FftLength, m.HopLength, m.Bins, m.NoiseQuantile);
            double binHz = (double)m.SampleRate / _fft;
            int start = (int)Math.Ceiling(m.MinHz / binHz), stop = (int)Math.Floor(m.MaxHz / binHz) + 1;
            if (stop - start != _bins) throw new InvalidDataException($"model expects {_bins} bins, range gives {stop - start}");
            _cos = new float[_bins * _fft];
            _sin = new float[_bins * _fft];
            for (int b = 0; b < _bins; b++)
                for (int n = 0; n < _fft; n++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / _fft);   // np.hanning(N+1)[:-1]
                    double a = -2 * Math.PI * (start + b) * n / _fft;
                    _cos[b * _fft + n] = (float)(w * Math.Cos(a));
                    _sin[b * _fft + n] = (float)(w * Math.Sin(a));
                }
            _rayleigh = Math.Sqrt(-2.0 * Math.Log(1.0 - _quantile));
        }

        /// <summary>Normalised log-magnitude spectrogram, [frames, bins] row-major.</summary>
        public (float[] Data, int Frames) Compute(float[] audio)
        {
            int pad = _fft / 2, n = audio.Length;
            // numpy "reflect" padding: mirror without repeating the edge sample.
            var x = new float[n + 2 * pad];
            for (int i = 0; i < pad; i++)
            {
                x[i] = audio[Math.Min(pad - i, n - 1)];
                x[pad + n + i] = audio[Math.Max(n - 2 - i, 0)];
            }
            Array.Copy(audio, 0, x, pad, n);
            int frames = 1 + (x.Length - _fft) / _hop;
            var spec = new float[frames * _bins];
            for (int f = 0; f < frames; f++)
            {
                var frame = x.AsSpan(f * _hop, _fft);
                for (int b = 0; b < _bins; b++)
                {
                    var c = _cos.AsSpan(b * _fft, _fft);
                    var s = _sin.AsSpan(b * _fft, _fft);
                    float re = 0, im = 0;
                    for (int i = 0; i < _fft; i++)
                    {
                        re += frame[i] * c[i];
                        im += frame[i] * s[i];
                    }
                    spec[f * _bins + b] = MathF.Sqrt(re * re + im * im);
                }
            }
            var sorted = (float[])spec.Clone();
            Array.Sort(sorted);
            double noise = Math.Max(Math.Max(sorted[(int)Math.Floor(_quantile * (sorted.Length - 1))] / _rayleigh,
                sorted[^1] * NoiseLevelMinRelativeToPeak), NoiseLevelMin);
            float inv = (float)(1.0 / noise);
            for (int i = 0; i < spec.Length; i++) spec[i] = MathF.Log(1 + spec[i] * inv);
            return (spec, frames);
        }
    }

    // ---- stitching overlapping windows and CTC decoding ----

    private sealed class Span(string ch, long start, long end)
    {
        public string Char { get; } = ch;
        public long StartFrame { get; set; } = start;
        public long EndFrame { get; set; } = end;
    }

    private readonly record struct LaneText(string Text, bool PrependSpace);

    private sealed class Stitcher
    {
        private readonly string[] _chars;
        private readonly int _blank, _space, _classes;
        private readonly double _framesPerSecond;
        private readonly int _maxSegmentSamples, _leftContextFrames, _freezeGuardFrames, _wordBoundaryFrames;
        public int HopLength { get; }

        private long _rowBase;
        private float[] _sums = [];     // [rows, classes]
        private float[] _weights = [];  // [rows]
        private int _rows;
        private long _coveredEnd, _frozenEnd, _committedEnd;
        private string _previousSymbol = "none";
        private long _blankFrames;
        private bool _hasCommittedText;

        public Stitcher(Meta m)
        {
            _chars = m.Chars;
            _blank = m.BlankIndex;
            _space = Array.IndexOf(_chars, " ");
            _classes = m.NumClasses;
            HopLength = m.HopLength * m.OutputTimeStride;
            _framesPerSecond = (double)m.SampleRate / HopLength;
            _maxSegmentSamples = (int)(MaxSegmentSeconds * m.SampleRate);
            _leftContextFrames = (int)Math.Round(StitchLeftContextSeconds * m.SampleRate / HopLength);
            _freezeGuardFrames = (int)Math.Round(StitchFreezeGuardSeconds * m.SampleRate / HopLength);
            _wordBoundaryFrames = (int)Math.Ceiling((double)WordBoundaryBlankHops / m.OutputTimeStride);
        }

        public long ContextStartFrame => Math.Max(0, _frozenEnd - _leftContextFrames);

        public long WindowStartFrame(long endSample) =>
            Math.Max(ContextStartFrame, (long)Math.Ceiling((endSample - _maxSegmentSamples) / (double)HopLength));

        public void AddWindow(long startFrame, float[] logits, int frameCount, bool isFinal)
        {
            if (frameCount <= 0)
            {
                if (isFinal) _frozenEnd = _coveredEnd;
                return;
            }
            long endFrame = startFrame + frameCount;
            EnsureRows(endFrame);
            var logProbs = DilatedLogProbs(logits, frameCount);
            for (long f = Math.Max(0, _frozenEnd - startFrame); f < frameCount; f++)
            {
                double left = startFrame == 0 ? double.PositiveInfinity : f / _framesPerSecond;
                double right = isFinal ? double.PositiveInfinity : (frameCount - 1 - f) / _framesPerSecond;
                float w = (float)Math.Max(MinViewWeight,
                    SmoothStep(left / StitchLeftRampSeconds) * SmoothStep(right / StitchRightRampSeconds));
                long row = startFrame + f - _rowBase;
                for (int c = 0; c < _classes; c++)
                    _sums[row * _classes + c] += w * logProbs[f * _classes + c];
                _weights[row] += w;
            }
            _coveredEnd = Math.Max(_coveredEnd, endFrame);
            _frozenEnd = isFinal ? _coveredEnd : Math.Max(_frozenEnd, endFrame - _freezeGuardFrames);
        }

        public LaneText? Take(bool final)
        {
            long startFrame = _committedEnd;
            if (_coveredEnd - startFrame <= 0) return null;
            bool startsWord = !_hasCommittedText || _previousSymbol == "space";
            var (spans, argmax) = DecodeCtcGreedy(ReadScores(startFrame, _coveredEnd), (int)(_coveredEnd - startFrame));
            foreach (var s in spans)
            {
                s.StartFrame += startFrame;
                s.EndFrame += startFrame;
            }
            var shifted = spans;

            double commitLimit = final ? double.PositiveInfinity : _frozenEnd;
            int committedCount = 0;
            for (int i = 0; i < shifted.Count; i++)
            {
                if (shifted[i].EndFrame + 1 >= commitLimit) break;
                if (!string.IsNullOrWhiteSpace(shifted[i].Char)) committedCount = i + 1;
            }

            LaneText? committed = null;
            if (committedCount > 0)
            {
                committed = ToLaneText(shifted.Take(committedCount), startsWord);
                _hasCommittedText = true;
                _committedEnd = shifted[committedCount - 1].EndFrame + 1;
                _previousSymbol = "char";
                _blankFrames = 0;
            }

            var remaining = shifted.Skip(committedCount).ToList();
            var next = remaining.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Char));
            double releaseEnd = Math.Min(final ? _coveredEnd : _frozenEnd,
                next is null ? double.PositiveInfinity : next.StartFrame) - 1;
            string previousSymbol = _previousSymbol;
            long blankFrames = _blankFrames;
            for (long frame = _committedEnd; frame < releaseEnd; frame++)
            {
                if (argmax[frame - startFrame] == _space)
                {
                    previousSymbol = "space";
                    blankFrames = 0;
                }
                else blankFrames++;
            }
            if (releaseEnd > _committedEnd)
            {
                _previousSymbol = previousSymbol;
                _blankFrames = blankFrames;
                _committedEnd = (long)releaseEnd;
            }
            DropRowsBefore(_committedEnd);
            return committed;
        }

        private (List<Span> Spans, int[] Argmax) DecodeCtcGreedy(float[] scores, int frameCount)
        {
            var argmax = new int[frameCount];
            for (int f = 0; f < frameCount; f++)
            {
                int best = 0;
                float bestV = float.NegativeInfinity;
                for (int c = 0; c < _classes; c++)
                {
                    float v = scores[f * _classes + c];
                    if (v > bestV) { bestV = v; best = c; }
                }
                argmax[f] = best;
            }
            var indices = (int[])argmax.Clone();
            if (_space >= 0)
            {
                int previous = _previousSymbol == "char" ? -2 : _previousSymbol == "space" ? _space : -1;
                int frame = 0;
                while (frame < frameCount)
                {
                    if (indices[frame] != _blank)
                    {
                        previous = indices[frame];
                        frame++;
                        continue;
                    }
                    int runStart = frame;
                    while (frame < frameCount && indices[frame] == _blank) frame++;
                    int following = frame < frameCount ? indices[frame] : -1;
                    long runLength = frame - runStart + (runStart == 0 ? _blankFrames : 0);
                    if (runLength >= _wordBoundaryFrames && previous != -1 && previous != _space
                        && following >= 0 && following != _space)
                        for (int i = runStart; i < frame; i++) indices[i] = _space;
                }
            }
            var spans = new List<Span>();
            int previousIndex = -1;
            Span? active = null;
            for (int f = 0; f < frameCount; f++)
            {
                int index = indices[f];
                if (index == _blank)
                {
                    previousIndex = -1;
                    active = null;
                    continue;
                }
                if (index == previousIndex)
                {
                    if (active is not null) active.EndFrame = f;
                    continue;
                }
                previousIndex = index;
                if (index >= _chars.Length)
                {
                    active = null;
                    continue;
                }
                active = new Span(_chars[index], f, f);
                spans.Add(active);
            }
            return (spans, argmax);
        }

        private static LaneText ToLaneText(IEnumerable<Span> spans, bool prependSpace)
        {
            var sb = new StringBuilder();
            bool leading = prependSpace, pending = false;
            foreach (var s in spans)
            {
                if (string.IsNullOrWhiteSpace(s.Char))
                {
                    if (sb.Length == 0) leading = true;
                    else pending = true;
                    continue;
                }
                if (pending)
                {
                    sb.Append(' ');
                    pending = false;
                }
                sb.Append(s.Char);
            }
            return new LaneText(sb.ToString(), leading);
        }

        private float[] DilatedLogProbs(float[] logits, int frames)
        {
            var lp = new float[logits.Length];
            for (int f = 0; f < frames; f++) LogSoftmax(logits.AsSpan(f * _classes, _classes), lp.AsSpan(f * _classes, _classes));
            var dil = new float[logits.Length];
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < _classes; c++)
                {
                    float m = float.NegativeInfinity;
                    if (c == _blank) m = lp[f * _classes + c];
                    else
                        for (int o = -StitchDilationFrames; o <= StitchDilationFrames; o++)
                        {
                            int g = f + o;
                            if (g >= 0 && g < frames) m = Math.Max(m, lp[g * _classes + c]);
                        }
                    dil[f * _classes + c] = m;
                }
            var outp = new float[logits.Length];
            for (int f = 0; f < frames; f++) LogSoftmax(dil.AsSpan(f * _classes, _classes), outp.AsSpan(f * _classes, _classes));
            return outp;
        }

        private static void LogSoftmax(ReadOnlySpan<float> v, Span<float> o)
        {
            float peak = float.NegativeInfinity;
            foreach (var x in v) peak = Math.Max(peak, x);
            double sum = 0;
            foreach (var x in v) sum += Math.Exp(x - peak);
            float lse = peak + (float)Math.Log(sum);
            for (int i = 0; i < v.Length; i++) o[i] = v[i] - lse;
        }

        private static double SmoothStep(double v)
        {
            double t = Math.Clamp(v, 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        private void EnsureRows(long endFrame)
        {
            long need = endFrame - _rowBase;
            if (need <= _rows) return;
            if (need > _weights.Length)
            {
                int cap = (int)Math.Max(need, _weights.Length * 2L);
                Array.Resize(ref _weights, cap);
                Array.Resize(ref _sums, cap * _classes);
            }
            Array.Clear(_weights, _rows, (int)need - _rows);
            Array.Clear(_sums, _rows * _classes, ((int)need - _rows) * _classes);
            _rows = (int)need;
        }

        private void DropRowsBefore(long frame)
        {
            int drop = (int)Math.Min(frame - _rowBase, _rows);
            if (drop <= 0) return;
            Array.Copy(_weights, drop, _weights, 0, _rows - drop);
            Array.Copy(_sums, drop * _classes, _sums, 0, (_rows - drop) * _classes);
            _rows -= drop;
            _rowBase += drop;
        }

        private float[] ReadScores(long startFrame, long endFrame)
        {
            int n = (int)(endFrame - startFrame);
            var scores = new float[n * _classes];
            for (int f = 0; f < n; f++)
            {
                long row = startFrame - _rowBase + f;
                float w = row >= 0 && row < _rows ? _weights[row] : 0;
                for (int c = 0; c < _classes; c++)
                    scores[f * _classes + c] = w > 0 ? _sums[row * _classes + c] / w
                        : c == _blank ? 0f : UncoveredLogProb;
            }
            return scores;
        }
    }
}
