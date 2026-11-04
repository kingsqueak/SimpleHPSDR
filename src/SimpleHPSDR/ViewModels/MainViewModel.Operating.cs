using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Controls;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Everyday operating controls: RIT, XIT, VFO lock, A/B swap,
/// squelch, RX leveler, manual notches and band stacking. RIT is on slice A
/// only (the engine's limit); XIT moves the transmit frequency.</summary>
public sealed partial class MainViewModel
{
    private const int OffsetStepHz = 10;
    private const long MaxOffsetHz = 99_999;
    private const double DefaultNotchHz = 100;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RitText))] private bool _ritOn;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RitText))] private long _ritHz;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(XitText))] private bool _xitOn;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(XitText))] private long _xitHz;
    [ObservableProperty] private bool _vfoLocked;
    [ObservableProperty] private bool _squelchOn;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SquelchText))] private double _squelchLevel;
    [ObservableProperty] private bool _squelchAdaptive = true;
    [ObservableProperty] private bool _rxLevelerOn;

    private Latest<long>? _ritSender, _xitSender;
    private Latest<int>? _squelchSender;
    private SquelchConfig _squelch = new();
    private bool _applyingOperating;
    private List<NotchDto> _notches = [];

    public string RitText => FormatOffset(RitHz);
    public string XitText => FormatOffset(XitHz);
    public string SquelchText => $"{SquelchLevel:0}";

    // The engine's adaptive squelch opens at its noise floor plus a fixed
    // margin and ignores the level: the 20th percentile of the last 12
    // readings of received audio, slewed up slowly and down quickly. The app
    // doesn't get those readings, so the mark applies the same method to the
    // S-meter: a guide to the opening point, not the exact value. Fixed
    // squelch has no level in dBm, so there the mark is only the level's
    // place along the bar.
    private const double AdaptiveMarginDb = 2.5, FloorRiseDb = 0.25, FloorFallDb = 7;
    private readonly double[] _squelchWindow = new double[12];
    private int _squelchFill, _squelchIndex;
    private double _squelchFloor = double.NaN;
    [ObservableProperty] private double _squelchMark = double.NaN;

    private void TrackSquelchMark(double dbm)
    {
        if (!double.IsFinite(dbm)) return;
        _squelchWindow[_squelchIndex] = dbm;
        _squelchIndex = (_squelchIndex + 1) % _squelchWindow.Length;
        if (_squelchFill < _squelchWindow.Length) _squelchFill++;
        var recent = _squelchWindow[.._squelchFill];
        Array.Sort(recent);
        double floor = recent[(int)Math.Round((_squelchFill - 1) * 0.2)];
        _squelchFloor = double.IsNaN(_squelchFloor) ? floor
            : floor > _squelchFloor ? Math.Min(floor, _squelchFloor + FloorRiseDb)
            : Math.Max(floor, _squelchFloor - FloorFallDb);
        SquelchMark = !SquelchOn ? double.NaN
            : SquelchAdaptive ? _squelchFloor + AdaptiveMarginDb
            : SMeterBar.MinDbm + SquelchLevel / 100 * (SMeterBar.MaxDbm - SMeterBar.MinDbm);
    }

    private static string FormatOffset(long hz) => hz == 0 ? "0" : $"{hz:+0;-0}";

    /// <summary>Notches to draw on the panadapter.</summary>
    public IReadOnlyList<NotchMark> NotchMarks() =>
        [.. _notches.Select(n => new NotchMark((long)n.CenterHz, n.WidthHz, n.Active))];

    /// <summary>From engine state (connect and the 1 s poll).</summary>
    private void UpdateOperating(StateDto s)
    {
        _applyingOperating = true;
        RitOn = s.RitEnabled;
        XitOn = s.XitEnabled;
        if (_ritSender is not { Busy: true }) RitHz = s.RitHz;
        if (_xitSender is not { Busy: true }) XitHz = s.XitHz;
        VfoLocked = s.VfoLocked;
        RxLevelerOn = s.RxLevelerEnabled;
        _squelch = s.Squelch ?? new SquelchConfig();
        SquelchOn = _squelch.Enabled;
        SquelchAdaptive = _squelch.Adaptive;
        if (_squelchSender is not { Busy: true }) SquelchLevel = _squelch.Level;
        _applyingOperating = false;
    }

    [RelayCommand]
    private Task ToggleRitAsync() => Try(async () =>
    {
        await _client!.SetRitAsync(on: !RitOn);
        RitOn = !RitOn;
    });

    [RelayCommand]
    private Task ToggleXitAsync() => Try(async () =>
    {
        await _client!.SetXitAsync(on: !XitOn);
        XitOn = !XitOn;
    });

    /// <summary>Wheel over RIT: 10 Hz a click.</summary>
    public void NudgeRit(int clicks)
    {
        if (!IsConnected) return;
        RitHz = Math.Clamp(RitHz + clicks * OffsetStepHz, -MaxOffsetHz, MaxOffsetHz);
        (_ritSender ??= new Latest<long>(hz => _client!.SetRitAsync(hz: hz))).Send(RitHz);
    }

    public void NudgeXit(int clicks)
    {
        if (!IsConnected) return;
        XitHz = Math.Clamp(XitHz + clicks * OffsetStepHz, -MaxOffsetHz, MaxOffsetHz);
        (_xitSender ??= new Latest<long>(hz => _client!.SetXitAsync(hz: hz))).Send(XitHz);
    }

    public void ClearRit() { if (IsConnected) { RitHz = 0; _ = Try(() => _client!.SetRitAsync(hz: 0)); } }
    public void ClearXit() { if (IsConnected) { XitHz = 0; _ = Try(() => _client!.SetXitAsync(hz: 0)); } }

    [RelayCommand]
    private Task ToggleVfoLockAsync() => Try(async () =>
    {
        await _client!.SetVfoLockAsync(!VfoLocked);
        VfoLocked = !VfoLocked;
        Status = VfoLocked ? "VFO locked." : "VFO unlocked.";
    });

    /// <summary>Swap the frequencies of slices A and B.</summary>
    [RelayCommand]
    private Task SwapSlicesAsync() => Try(async () =>
    {
        await _client!.SwapVfosAsync();
        await MakeActiveAsync(ActiveRx);
    });

    [RelayCommand]
    private void ToggleSquelch()
    {
        SquelchOn = !SquelchOn;
        SendSquelch();
    }

    partial void OnSquelchLevelChanged(double value) => SendSquelch();
    partial void OnSquelchAdaptiveChanged(bool value) => SendSquelch();

    private void SendSquelch()
    {
        if (_applyingOperating || !IsConnected) return;
        _squelchSender ??= new Latest<int>(_ => _client!.SetSquelchAsync(_squelch));
        _squelch = _squelch with { Enabled = SquelchOn, Level = (int)Math.Round(SquelchLevel), Adaptive = SquelchAdaptive };
        _squelchSender.Send(_squelch.GetHashCode());
    }

    [RelayCommand]
    private Task ToggleRxLevelerAsync() => Try(async () =>
    {
        await _client!.SetRxLevelerAsync(!RxLevelerOn);
        RxLevelerOn = !RxLevelerOn;
    });

    // ---- notches --------------------------------------------------------

    private async Task LoadNotchesAsync()
    {
        try { _notches = await _client!.GetNotchesAsync(); }
        catch (Exception) { _notches = []; }
    }

    /// <summary>The notch under a right-click, if any.</summary>
    public NotchDto? NotchAt(long hz) =>
        _notches.FirstOrDefault(n => Math.Abs(n.CenterHz - hz) <= Math.Max(n.WidthHz / 2, DefaultNotchHz / 2));

    public Task AddNotchAsync(long hz) => SendNotchesAsync([.. _notches, new NotchDto(hz, DefaultNotchHz)]);

    public Task RemoveNotchAsync(NotchDto notch) => SendNotchesAsync([.. _notches.Where(n => n != notch)]);

    public Task SetNotchWidthAsync(NotchDto notch, double hz) =>
        SendNotchesAsync([.. _notches.Select(n => n == notch ? n with { WidthHz = hz } : n)]);

    public Task ClearNotchesAsync() => SendNotchesAsync([]);

    public bool HasNotches => _notches.Count > 0;

    private Task SendNotchesAsync(List<NotchDto> notches) => Try(async () =>
    {
        await _client!.SetNotchesAsync(notches);
        _notches = notches;
    });

    // ---- band stacking --------------------------------------------------

    private string BandKey => SimpleHPSDR.Band.Find(VfoHz)?.Name ?? "GEN";

    /// <summary>Saved frequencies for the current band, newest first.</summary>
    public async Task<List<BandStackEntryDto>> BandStackAsync()
    {
        if (!IsConnected) return [];
        var all = await _client!.GetBandStackAsync();
        return [.. all.Where(e => e.Band == BandKey).OrderByDescending(e => e.UpdatedUtcMs)];
    }

    public Task SaveToBandStackAsync() => Try(async () =>
    {
        await _client!.AddBandStackAsync(BandKey, new BandStackAddRequest(
            $"{VfoHz / 1e6:0.000000} {Mode}", VfoHz, Mode, FilterLowHz, FilterHighHz));
        Status = $"Saved {VfoHz / 1e6:0.000000} MHz {Mode}.";
    });

    public Task RecallBandStackAsync(BandStackEntryDto e) => Try(async () =>
    {
        TuneTo(e.Hz);
        if (e.Mode != Mode) await SelectModeAsync(e.Mode);
        if (e is not { FilterLowHz: { } low, FilterHighHz: { } high }) return;
        // Light the filter button when the saved filter is one of them.
        if (Filters.FirstOrDefault(f => f.Preset.LowHz == low && f.Preset.HighHz == high) is { } slot)
            await SelectFilterAsync(slot);
        else
            DragFilter(low, high);
    });

    public Task RemoveBandStackAsync(BandStackEntryDto e) => Try(() => _client!.RemoveBandStackAsync(e.Band, e.Id));
}
