using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

public sealed record AsioInputOption(string Label, int Channel, int Width)
{
    public override string ToString() => Label;
}

/// <summary>ASIO on the Audio page. The engine only offers it on Windows with
/// a driver installed, so everywhere else none of this shows. The channel
/// lists come from the selected driver, and the engine can only read those
/// once it has that driver, so every change is sent and the lists are refilled
/// from the reply.</summary>
public sealed partial class SettingsViewModel
{
    private const string SystemBackend = "system", AsioBackend = "asio";
    private const int AsioRateHz = 48000;   // the engine requires it

    [ObservableProperty] private bool _hasAsio;
    [ObservableProperty] private bool _asioActive;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseAsio), nameof(UseSystemAudio))]
    private DeviceOption? _selectedBackend;
    [ObservableProperty] private DeviceOption? _selectedAsioDriver;
    [ObservableProperty] private ChannelOption? _selectedAsioOutput;
    [ObservableProperty] private AsioInputOption? _selectedAsioInput;
    [ObservableProperty] private ChannelOption? _selectedAsioBuffer;

    public bool UseAsio => SelectedBackend?.Id == AsioBackend;
    public bool UseSystemAudio => !UseAsio;

    public DeviceOption[] Backends { get; } =
        [new("System audio", SystemBackend), new("ASIO", AsioBackend)];
    public ObservableCollection<DeviceOption> AsioDrivers { get; } = [];
    public ObservableCollection<ChannelOption> AsioOutputs { get; } = [];
    public ObservableCollection<AsioInputOption> AsioInputs { get; } = [];
    public ObservableCollection<ChannelOption> AsioBuffers { get; } = [];

    private sealed record AsioSelection(
        string? Backend, string? DriverId, int? Input, int? Width, int? Output, int? Buffer);
    private AsioSelection? _asioWas;

    private AsioSelection CurrentAsio() => new(
        SelectedBackend?.Id, SelectedAsioDriver?.Id, SelectedAsioInput?.Channel, SelectedAsioInput?.Width,
        SelectedAsioOutput?.Value, SelectedAsioBuffer?.Value);

    private void PopulateAsio(AudioDevices d)
    {
        bool active = d.Backend == AsioBackend;
        HasAsio = active || d.AvailableBackends?.Contains(AsioBackend) == true;
        AsioActive = active;
        SelectedBackend = active ? Backends[1] : Backends[0];

        AsioDrivers.Clear();
        foreach (var driver in d.AsioDrivers ?? []) AsioDrivers.Add(new DeviceOption(driver.Name, driver.Id));
        SelectedAsioDriver = AsioDrivers.FirstOrDefault(o => o.Id == d.AsioDriverId) ?? AsioDrivers.FirstOrDefault();

        AsioOutputs.Clear();
        var outs = d.AsioOutputs ?? [];
        foreach (var (a, b) in outs.Zip(outs.Skip(1)))
            if (b.Index == a.Index + 1 && (a.Index % 2 == 0 || a.Index == d.AsioOutputChannel))
                AsioOutputs.Add(new ChannelOption($"{a.Name} + {b.Name}", a.Index));
        SelectedAsioOutput = AsioOutputs.FirstOrDefault(o => o.Value == d.AsioOutputChannel)
                             ?? AsioOutputs.FirstOrDefault();

        AsioInputs.Clear();
        var ins = d.AsioInputs ?? [];
        foreach (var c in ins) AsioInputs.Add(new AsioInputOption(c.Name, c.Index, 1));
        foreach (var (a, b) in ins.Zip(ins.Skip(1)))
            if (b.Index == a.Index + 1 && a.Index % 2 == 0)
                AsioInputs.Add(new AsioInputOption($"{a.Name} + {b.Name}", a.Index, 2));
        SelectedAsioInput = AsioInputs.FirstOrDefault(i => i.Channel == d.AsioInputChannel && i.Width == d.AsioInputWidth)
                            ?? AsioInputs.FirstOrDefault();

        AsioBuffers.Clear();
        AsioBuffers.Add(new ChannelOption(
            d.AsioBufferPreferredFrames is { } preferred ? $"Driver default ({Frames((int)preferred)})" : "Driver default", 0));
        foreach (var n in BufferSizes(d.AsioBufferMinFrames, d.AsioBufferMaxFrames, d.AsioBufferGranularity))
            AsioBuffers.Add(new ChannelOption(Frames(n), n));
        if (d.AsioRequestedBufferFrames > 0 && AsioBuffers.All(b => b.Value != d.AsioRequestedBufferFrames))
            AsioBuffers.Add(new ChannelOption(Frames(d.AsioRequestedBufferFrames), d.AsioRequestedBufferFrames));
        SelectedAsioBuffer = AsioBuffers.First(b => b.Value == d.AsioRequestedBufferFrames);

        _asioWas = CurrentAsio();
    }

    private static string Frames(int n) => $"{n} frames, {n * 1000.0 / AsioRateHz:0.#} ms";

    /// <summary>The sizes a driver accepts, by the ASIO rule: granularity -1
    /// means powers of two, 0 only the preferred size (so nothing to offer
    /// beyond the default), otherwise steps up from the minimum.</summary>
    internal static List<int> BufferSizes(uint? min, uint? max, int? granularity)
    {
        var sizes = new List<int>();
        if (min is not { } lo || max is not { } hi || lo == 0 || granularity is not { } g || g == 0) return sizes;
        if (g > 0)
            for (long n = lo; n <= hi; n += g) sizes.Add((int)n);
        else
            for (long n = 1; n <= hi; n *= 2)
                if (n >= lo) sizes.Add((int)n);
        // A driver stepping in small units would make a very long list.
        if (sizes.Count > 12)
            sizes = sizes.Where((n, i) => (n & (n - 1)) == 0 || i == 0 || i == sizes.Count - 1).ToList();
        return sizes;
    }

    private async Task ApplyRouteAsync(DeviceSelection sel, AsioSelection asio)
    {
        // A new driver has its own channels and buffers: let the engine choose.
        bool newDriver = asio.DriverId != _asioWas?.DriverId;
        var request = new AudioRouteRequest(
            sel.InputId, sel.OutputId, sel.Channel, asio.Backend ?? SystemBackend, asio.DriverId,
            newDriver ? 0 : asio.Input, newDriver ? 1 : asio.Width,
            newDriver ? null : asio.Output, newDriver ? null : asio.Buffer);
        try
        {
            ShowRoute(await _client.SetAudioRouteAsync(request));
        }
        catch (Exception)
        {
            // The engine has gone back to the previous route: show that.
            ShowRoute(await _client.GetAudioDevicesAsync());
            throw;
        }
    }

    private void ShowRoute(AudioDevices devices)
    {
        bool was = _populating;
        _populating = true;
        try { PopulateAsio(devices); }
        finally { _populating = was; }
        _devicesWas = CurrentDevices();
    }

    [RelayCommand]
    private async Task OpenAsioPanelAsync()
    {
        try
        {
            await _client.OpenAsioPanelAsync();
            await _applyGate.WaitAsync();
            try
            {
                var devices = await _client.GetAudioDevicesAsync();
                ShowRoute(devices);
                await RefreshAudioStatusAsync(devices);
            }
            finally { _applyGate.Release(); }
        }
        catch (Exception ex)
        {
            Status = $"Could not open the driver panel: {ex.Message}";
        }
    }
}
