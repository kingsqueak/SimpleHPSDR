using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>Configuration > Hardware: options only some radios have. Every
/// section stays on the page and is greyed out unless the radio that's set
/// has that hardware, going by what the engine reports for it. Values apply
/// as they change. The Hermes-Lite 2 IO board: status, per-band antennas and
/// its antenna tuner (its manual outputs are left to the board's firmware).</summary>
public sealed partial class SettingsViewModel
{
    private sealed record HardwareValues(bool Dither, bool Random, bool BandVolts, bool Codec, int Gpio);

    private HardwareValues? _hardwareOrig;
    private bool _hardwareLoading;

    [ObservableProperty] private string _hardwareText = "";
    [ObservableProperty] private bool _hasAdcOptions;
    [ObservableProperty] private bool _hasHl2Options;
    [ObservableProperty] private bool _hasAnvelinaDx;
    [ObservableProperty] private string _hl2IoBoardText = "";

    [ObservableProperty] private bool _adcDither;
    [ObservableProperty] private bool _adcRandomizer;
    [ObservableProperty] private bool _hl2BandVolts;
    [ObservableProperty] private bool _hl2PlusCodec;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hl2Gpio1), nameof(Hl2Gpio2), nameof(Hl2Gpio3), nameof(Hl2Gpio4))]
    private int _hl2GpioBits;

    public bool Hl2Gpio1 { get => GpioBit(0); set => SetGpioBit(0, value); }
    public bool Hl2Gpio2 { get => GpioBit(1); set => SetGpioBit(1, value); }
    public bool Hl2Gpio3 { get => GpioBit(2); set => SetGpioBit(2, value); }
    public bool Hl2Gpio4 { get => GpioBit(3); set => SetGpioBit(3, value); }

    private bool GpioBit(int bit) => (Hl2GpioBits & (1 << bit)) != 0;
    private void SetGpioBit(int bit, bool on) =>
        Hl2GpioBits = on ? Hl2GpioBits | (1 << bit) : Hl2GpioBits & ~(1 << bit);

    /// <summary>Reads what the engine has for the radio that's set. Runs when
    /// the window opens and when Radio model changes.</summary>
    private async Task LoadHardwareAsync()
    {
        var board = await _client.GetBoardFlagsAsync();
        var g2 = await _client.GetG2OptionsAsync();
        var hl2 = await _client.GetHl2OptionsAsync();
        var codec = await _client.GetHl2CodecAsync();
        var gpio = await _client.GetHl2GpioAsync();
        var io = await _client.GetHl2IoBoardAsync();
        var name = await _client.GetConnectedBoardAsync();

        _hardwareLoading = true;
        HasAdcOptions = g2.Supported;   // also Protocol 1 radios while connected
        HasHl2Options = board.HasHl2OptionalToggles;
        HasAnvelinaDx = board.SupportsAnvelinaDxOc;
        AdcDither = g2.DitherEnabled;
        AdcRandomizer = g2.RandomEnabled;
        Hl2BandVolts = hl2.BandVolts;
        Hl2PlusCodec = codec.Enabled;
        Hl2GpioBits = gpio.Bits;
        _hardwareLoading = false;

        _hardwareOrig ??= new HardwareValues(AdcDither, AdcRandomizer, Hl2BandVolts, Hl2PlusCodec, Hl2GpioBits);
        if (name == RadioModels.BoardName && RadioModelIndex >= 0) name = RadioModelNames[RadioModelIndex];
        HardwareText = name == "Unknown"
            ? "Connect to a radio to see its settings here."
            : HasAdcOptions || HasHl2Options || HasAnvelinaDx
                ? $"Settings for your {name}."
                : $"Your {name} has no extra settings here.";
        ApplyIoBoard(io);
    }

    // ---- HL2 IO board -------------------------------------------------

    /// <summary>One band's antennas, shown 1 to 16 (the engine and board count from 0).</summary>
    public sealed partial class IoBandRow(SettingsViewModel owner, StationClient.Hl2IoBoardBand band) : ObservableObject
    {
        public string Band { get; } = band.Band;
        [ObservableProperty] private decimal _rx = band.RxAntenna + 1;
        [ObservableProperty] private decimal _tx = band.TxAntenna + 1;
        partial void OnRxChanged(decimal value) => owner.SendIoBand(this);
        partial void OnTxChanged(decimal value) => owner.SendIoBand(this);
        internal StationClient.Hl2IoBoardBand ToBand() =>
            new(Band, (byte)Math.Clamp((int)Rx - 1, 0, 15), (byte)Math.Clamp((int)Tx - 1, 0, 15));
    }

    [ObservableProperty] private bool _hasIoBoard;
    [ObservableProperty] private string _ioInputsText = "";
    [ObservableProperty] private string _ioTunerText = "";
    [ObservableProperty] private bool _ioTunerBusy;
    public ObservableCollection<IoBandRow> IoBands { get; } = [];
    private bool _ioLoading;
    private DispatcherTimer? _ioTimer;

    private void ApplyIoBoard(StationClient.Hl2IoBoardView view)
    {
        var io = view.Status;
        Hl2IoBoardText = !HasHl2Options ? "Not available on this radio."
            : !io.Supported ? "Shown once the radio is connected."
            : io.Present ? $"Found: hardware {io.HardwareVersion}, firmware {io.FirmwareMajor}.{io.FirmwareMinor}"
                + (io.Fault != 0 ? $". Fault code {io.Fault}." : ", no faults.")
            : "Not found.";
        HasIoBoard = HasHl2Options && io.Present;
        string[] names = ["Ext TR", "In1", "In2", "In3", "In4", "In5"];
        var on = names.Where((_, bit) => (io.Inputs & (1 << bit)) != 0).ToArray();
        IoInputsText = on.Length == 0 ? "Inputs: none on." : $"Inputs on: {string.Join(", ", on)}.";
        IoTunerBusy = view.Tuning is "waiting" or "tuning" or "finishing";
        IoTunerText = view.Tuning switch
        {
            "waiting" => "Starting the tuner…",
            "tuning" => "Tuning: transmitting a carrier for the tuner…",
            "finishing" => "Finishing…",
            "complete" => "Tuned.",
            "timed-out" => "The tuner took longer than 15 seconds; stopped.",
            "cancelled" => "Stopped.",
            "disconnected" => "The IO board stopped answering.",
            "failed" => $"Tuning failed{(view.TunerError is { } e ? $": {e}" : ".")}",
            _ => "Ready.",
        };
        if (_ioLoading) return;
        _ioLoading = true;
        if (IoBands.Count == 0 || !IoBands.Select(b => b.Band).SequenceEqual(view.Bands.Select(b => b.Band)))
        {
            IoBands.Clear();
            foreach (var b in view.Bands) IoBands.Add(new IoBandRow(this, b));
        }
        _ioLoading = false;
    }

    internal void SendIoBand(IoBandRow row)
    {
        if (_ioLoading || _hardwareLoading) return;
        SendHardware(() => _client.SetHl2IoBandAsync(row.ToBand()));
    }

    [RelayCommand] private Task IoTuneAsync() => IoCommandAsync("tune");
    [RelayCommand] private Task IoBypassAsync() => IoCommandAsync("bypass");
    [RelayCommand] private Task IoStopAsync() => IoCommandAsync("cancel");

    private async Task IoCommandAsync(string action)
    {
        try
        {
            ApplyIoBoard(await _client.Hl2IoCommandAsync(action));
            if (action == "tune") WatchTuner();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            IoTunerText = $"Not done: {ex.Message}";
        }
    }

    // While the tuner works, follow its progress.
    private void WatchTuner()
    {
        _ioTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _ioTimer.Tick -= OnIoTick;
        _ioTimer.Tick += OnIoTick;
        _ioTimer.Start();
    }

    private async void OnIoTick(object? sender, EventArgs e)
    {
        try
        {
            ApplyIoBoard(await _client.GetHl2IoBoardAsync());
            if (!IoTunerBusy) _ioTimer?.Stop();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            _ioTimer?.Stop();
        }
    }

    partial void OnAdcDitherChanged(bool value) => SendHardware(() => _client.SetG2OptionsAsync(dither: value));
    partial void OnAdcRandomizerChanged(bool value) => SendHardware(() => _client.SetG2OptionsAsync(random: value));
    partial void OnHl2BandVoltsChanged(bool value) => SendHardware(() => _client.SetHl2OptionsAsync(value));
    partial void OnHl2PlusCodecChanged(bool value) => SendHardware(() => _client.SetHl2CodecAsync(value));
    partial void OnHl2GpioBitsChanged(int value) => SendHardware(() => _client.SetHl2GpioAsync(value));

    private async void SendHardware(Func<Task> send)
    {
        if (_hardwareLoading) return;
        // One at a time, with the other pages' saves too: settings written to
        // the engine at the same moment can corrupt its database and crash it.
        await _applyGate.WaitAsync();
        try
        {
            await send();
            Status = "Changes apply as you make them.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException)
        {
            Status = $"Not applied: {ex.Message}";
        }
        finally { _applyGate.Release(); }
    }

    private void UndoHardware()
    {
        if (_hardwareOrig is not { } o) return;
        if (HasAdcOptions) (AdcDither, AdcRandomizer) = (o.Dither, o.Random);
        if (HasHl2Options) (Hl2BandVolts, Hl2PlusCodec, Hl2GpioBits) = (o.BandVolts, o.Codec, o.Gpio);
    }
}
