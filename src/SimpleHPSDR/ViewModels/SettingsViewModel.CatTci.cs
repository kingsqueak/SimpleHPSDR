using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One serial CAT port (the engine has four, like Thetis CAT1-4).</summary>
public sealed partial class SerialCatRow(int index, CatSerialPortConfig cfg) : ObservableObject
{
    [ObservableProperty] private bool _enabled = cfg.Enabled;
    [ObservableProperty] private string _portName = cfg.PortName;
    [ObservableProperty] private int _baudRate = cfg.BaudRate;
    [ObservableProperty] private string _format = $"{cfg.DataBits}{cfg.Parity[0]}{(cfg.StopBits == "Two" ? 2 : 1)}";
    [ObservableProperty] private bool _autoReport = cfg.AutoReport;
    [ObservableProperty] private string _state = "";

    public string Label { get; } = $"CAT {index + 1}";

    public CatSerialPortConfig ToConfig()
    {
        // "8N1" style: data bits, parity letter, stop bits.
        int data = Format.Length > 0 && char.IsDigit(Format[0]) ? Format[0] - '0' : 8;
        string parity = (Format.Length > 1 ? Format[1] : 'N') switch
        {
            'E' => "Even", 'O' => "Odd", 'M' => "Mark", 'S' => "Space", _ => "None",
        };
        string stop = Format.EndsWith('2') ? "Two" : "One";
        return new CatSerialPortConfig(Enabled, PortName.Trim(), BaudRate, parity, data, stop, AutoReport);
    }
}

/// <summary>
/// Configuration > CAT &amp; TCI: the engine's TCI server, network CAT server
/// (Kenwood TS-2000 + Thetis ZZ commands) and four serial CAT ports. The
/// network servers are read at engine start, so a change there offers
/// "Restart engine to apply"; serial ports reconnect at once.
/// </summary>
public sealed partial class SettingsViewModel
{
    public const string LocalOnly = "127.0.0.1";
    public const string AllInterfaces = "0.0.0.0";

    /// <summary>Set by the main window: restart the engine and reconnect.</summary>
    public Func<Task>? RestartEngine { get; init; }

    [ObservableProperty] private bool _tciEnabled;
    [ObservableProperty] private bool _tciNetwork;          // listen on all interfaces
    [ObservableProperty] private double _tciPort = 40001;
    [ObservableProperty] private string _tciState = "";

    [ObservableProperty] private bool _catEnabled;
    [ObservableProperty] private bool _catNetwork;
    [ObservableProperty] private double _catPort = 19090;
    [ObservableProperty] private bool _catAutoReport;
    [ObservableProperty] private string _catState = "";

    [ObservableProperty] private bool _restartNeeded;

    public ObservableCollection<SerialCatRow> SerialCats { get; } = [];
    public ObservableCollection<string> SerialPortNames { get; } = [];
    public int[] BaudRates { get; } = CatSerialDefaults.BaudRates;
    public string[] SerialFormats { get; } = ["8N1", "8N2", "8E1", "8O1", "7E1", "7O1"];

    private TciRuntimeConfig? _tciWas, _tciOrig;
    private CatRuntimeConfig? _catWas, _catOrig;
    private CatSerialConfig? _serialWas, _serialOrig;

    private async Task LoadCatTciAsync()
    {
        var tci = await _client.GetTciStatusAsync();
        var cat = await _client.GetCatStatusAsync();
        var serial = await _client.GetCatSerialStatusAsync();

        // Show what the engine will run next (pending); status says what runs now.
        _tciWas = new TciRuntimeConfig(tci.PendingEnabled, tci.PendingBindAddress, tci.PendingPort);
        TciEnabled = tci.PendingEnabled;
        TciNetwork = tci.PendingBindAddress != LocalOnly;
        TciPort = tci.PendingPort;

        _catWas = new CatRuntimeConfig(cat.PendingEnabled, cat.PendingBindAddress, cat.PendingPort, cat.PendingAutoReport);
        CatEnabled = cat.PendingEnabled;
        CatNetwork = cat.PendingBindAddress != LocalOnly;
        CatPort = cat.PendingPort;
        CatAutoReport = cat.PendingAutoReport;

        SerialPortNames.Clear();
        foreach (var p in serial.AvailablePorts) SerialPortNames.Add(p);
        SerialCats.Clear();
        var configs = serial.Ports.Select(p => new CatSerialPortConfig(
            p.Enabled, p.PortName, p.BaudRate, p.Parity, p.DataBits, p.StopBits, p.AutoReport)).ToList();
        _serialWas = new CatSerialConfig(configs);
        for (int i = 0; i < configs.Count; i++)
            SerialCats.Add(Watch(new SerialCatRow(i, configs[i]), Group.Serial));

        (_tciOrig, _catOrig, _serialOrig) = (_tciWas, _catWas, _serialWas);
        ShowCatTciStatus(tci, cat, serial);
    }

    /// <summary>For Undo changes: put the fields back as they were when the
    /// window opened; the apply pass then sends whatever differs.</summary>
    private void RestoreCatTciFields()
    {
        if (_tciOrig is { } t)
            (TciEnabled, TciNetwork, TciPort) = (t.Enabled, t.BindAddress != LocalOnly, t.Port);
        if (_catOrig is { } c)
            (CatEnabled, CatNetwork, CatPort, CatAutoReport) = (c.Enabled, c.BindAddress != LocalOnly, c.Port, c.AutoReport);
        if (_serialOrig is { } s)
            for (int i = 0; i < s.Ports.Count && i < SerialCats.Count; i++)
            {
                var p = s.Ports[i];
                var row = SerialCats[i];
                (row.Enabled, row.PortName, row.BaudRate, row.AutoReport) = (p.Enabled, p.PortName, p.BaudRate, p.AutoReport);
                row.Format = $"{p.DataBits}{p.Parity[0]}{(p.StopBits == "Two" ? 2 : 1)}";
            }
    }

    private void ShowCatTciStatus(TciStatus tci, CatStatus cat, CatSerialStatus serial)
    {
        TciState = ServerState(tci.CurrentlyEnabled, tci.CurrentBindAddress, tci.CurrentPort, tci.ClientCount,
            tci.RequiresRestart, tci.PortAvailable, tci.Error);
        CatState = ServerState(cat.CurrentlyEnabled, cat.CurrentBindAddress, cat.CurrentPort, cat.ClientCount,
            cat.RequiresRestart, cat.PortAvailable, cat.Error);
        RestartNeeded = tci.RequiresRestart || cat.RequiresRestart;
        foreach (var s in serial.Ports)
            if (s.Index < SerialCats.Count)
                SerialCats[s.Index].State = s.Error is { Length: > 0 } e ? $"error: {e}"
                    : !s.Enabled ? "off" : s.Open ? "open" : "not open";
    }

    private static string ServerState(bool running, string bind, int port, int clients, bool restart,
        bool portFree, string? error)
    {
        string now = running
            ? $"Running on {(bind == LocalOnly ? "this computer" : "the network")}, port {port}, " +
              $"{clients} client{(clients == 1 ? "" : "s")}"
            : "Off";
        if (error is { Length: > 0 }) now += $" · {error}";
        if (restart) now += " · press Apply now to use your changes";
        return now;
    }

    private async Task ApplyTciAsync()
    {
        // Never loaded (the read failed): sending would replace the engine's
        // settings with this page's defaults.
        if (_tciWas is null) return;
        var cfg = new TciRuntimeConfig(TciEnabled, TciNetwork ? AllInterfaces : LocalOnly, (int)Math.Round(TciPort));
        if (cfg == _tciWas) return;
        await _client.SetTciConfigAsync(cfg);
        _tciWas = cfg;
        await RefreshCatTciStatusAsync();
    }

    private async Task ApplyCatAsync()
    {
        var cfg = new CatRuntimeConfig(CatEnabled, CatNetwork ? AllInterfaces : LocalOnly,
            (int)Math.Round(CatPort), CatAutoReport);
        if (_catWas is null || cfg == _catWas) return;
        await _client.SetCatConfigAsync(cfg);
        _catWas = cfg;
        await RefreshCatTciStatusAsync();
    }

    // The engine replaces all four ports together and reconnects them live.
    private async Task ApplySerialAsync()
    {
        if (_serialWas is not { } was) return;   // never loaded: would wipe all four ports
        var cfg = new CatSerialConfig(SerialCats.Select(r => r.ToConfig()).ToList());
        if (cfg.Ports.SequenceEqual(was.Ports)) return;
        await _client.SetCatSerialConfigAsync(cfg);
        _serialWas = cfg;
        await Task.Delay(500);   // let the ports open before showing their state
        await RefreshCatTciStatusAsync();
    }

    // Radio buttons set the choice through commands: a negated two-way
    // binding would fight between the pair.
    [RelayCommand] private void SetTciLocal() => TciNetwork = false;
    [RelayCommand] private void SetTciNetwork() => TciNetwork = true;
    [RelayCommand] private void SetCatLocal() => CatNetwork = false;
    [RelayCommand] private void SetCatNetwork() => CatNetwork = true;

    [RelayCommand]
    private async Task RefreshCatTciStatusAsync()
    {
        try
        {
            ShowCatTciStatus(await _client.GetTciStatusAsync(), await _client.GetCatStatusAsync(),
                await _client.GetCatSerialStatusAsync());
        }
        catch (Exception ex)
        {
            Status = $"Could not read CAT/TCI status: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TestTciPortAsync()
    {
        try
        {
            var r = await _client.TestTciPortAsync(TciNetwork ? AllInterfaces : LocalOnly, (int)Math.Round(TciPort));
            Status = r.Ok ? $"TCI port {TciPort:0} is free." : $"TCI port {TciPort:0}: {r.Error}";
        }
        catch (Exception ex) { Status = $"Could not test the port: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task TestCatPortAsync()
    {
        try
        {
            var r = await _client.TestCatPortAsync(CatNetwork ? AllInterfaces : LocalOnly, (int)Math.Round(CatPort));
            Status = r.Ok ? $"CAT port {CatPort:0} is free." : $"CAT port {CatPort:0}: {r.Error}";
        }
        catch (Exception ex) { Status = $"Could not test the port: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task RestartEngineNowAsync()
    {
        if (RestartEngine is null) return;
        Status = "Applying. The radio reconnects in a few seconds…";
        await RestartEngine();
        await RefreshCatTciStatusAsync();
        Status = "Done.";
    }
}
