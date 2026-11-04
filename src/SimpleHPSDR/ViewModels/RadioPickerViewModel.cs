using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

public sealed record RadioChoice(string Ip, bool Protocol2, byte? BoardId, string? Mac, string Label, bool TakeOver = false);

/// <summary>The radios that share the OrionMkII board ID and can't be told
/// apart on the network. Calibration, PA and PureSignal defaults depend on it.</summary>
public static class RadioModels
{
    public const string BoardName = "OrionMkII";
    /// <summary>Sent with a typed address. The ID means OrionMkII on both
    /// protocols; a discovered radio may report another on Protocol 2.</summary>
    public const byte OrionBoardId = 0x0A;
    public static string[] Names { get; } =
        ["ANAN-G2", "ANAN-G2-1K", "ANAN-7000DLE", "ANAN-8000DLE", "Orion MkII", "Anvelina Pro3", "Red Pitaya"];

    // The list's order is the engine's, and the names here are for people.
    public static int IndexOf(string engineName) =>
        Enum.TryParse<OrionMkIIVariant>(engineName, out var v) ? (int)v : 0;
}

public sealed class RadioEntry(RadioInfo info)
{
    public RadioInfo Info { get; } = info;
    public bool Protocol2 => Detail("protocol") == "P2";
    public string Title => $"{Info.BoardId}  ·  {(Protocol2 ? "Protocol 2" : "Protocol 1")}";
    public string Subtitle
    {
        get
        {
            var rx = Detail("numReceivers");
            return $"{Info.IpAddress}   firmware {Info.FirmwareVersion}" + (rx is null ? "" : $"   {rx} receivers");
        }
    }
    public string Mac => Info.MacAddress;
    public bool Busy => Info.Busy;
    public byte? RawBoardId =>
        Detail("rawBoardId") is { } s && s.StartsWith("0x") ? Convert.ToByte(s[2..], 16) : null;

    private string? Detail(string key) => Info.Details?.GetValueOrDefault(key);

    public RadioChoice ToChoice() => new(Info.IpAddress, Protocol2, RawBoardId, Mac, $"{Info.BoardId} {Info.IpAddress}");
}

public sealed partial class RadioPickerViewModel : ObservableObject
{
    private readonly StationClient _client;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(TakeOverCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedBusy))]
    [NotifyCanExecuteChangedFor(nameof(FirmwareCommand))]
    [NotifyPropertyChangedFor(nameof(ShowModel))]
    private RadioEntry? _selected;
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(ShowModel))] [NotifyPropertyChangedFor(nameof(ModelChoices))]
    private string _manualIp = "";
    [ObservableProperty] private int _modelIndex = -1;
    [ObservableProperty] private bool _manualP2 = true;
    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private bool _firewallBlocked;
    [ObservableProperty] private bool _noRadios;
    private bool _firewallHelpShown;

    public ObservableCollection<RadioEntry> Radios { get; } = [];
    public bool SelectedBusy => Selected is { Busy: true };
    private bool Manual => System.Net.IPAddress.TryParse(ManualIp.Trim(), out _);
    private const string OtherRadio = "Other radio";

    /// <summary>A typed address may be any radio, so it also offers "Other".</summary>
    public string[] ModelChoices => Manual ? [.. RadioModels.Names, OtherRadio] : RadioModels.Names;
    public bool ShowModel => Manual || Selected?.Info.BoardId.ToString() == RadioModels.BoardName;
    private int _engineModel;
    public event Action<RadioChoice?>? Done;
    /// <summary>Set by the window: shows the firmware window.</summary>
    public Func<FirmwareViewModel, Task>? ShowFirmware { get; set; }
    /// <summary>Set by the window once it's open: shows the ports and protocols.</summary>
    public Func<Task>? ShowNetworkHelp { get; set; }

    public RadioPickerViewModel(StationClient client, bool autoConnect)
    {
        _client = client;
        _autoConnect = autoConnect;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        Searching = true;
        try
        {
            _engineModel = RadioModels.IndexOf((await _client.GetRadioVariantAsync()).Variant);
            ModelIndex = _engineModel;
        }
        catch (Exception) { }
        Status = "Searching the network…";
        bool blocked = false;
        try
        {
            var found = await _client.DiscoverAsync();
            Radios.Clear();
            foreach (var r in found) Radios.Add(new RadioEntry(r));
            Selected = Radios.FirstOrDefault(r => !r.Busy) ?? Radios.FirstOrDefault();
            Status = Radios.Count switch
            {
                0 => "No radios found. Check the radio is on and on this network, or enter its address.",
                1 => "Found 1 radio.",
                var n => $"Found {n} radios.",
            };
            blocked = await CheckFirewallAsync();
        }
        catch (Exception ex)
        {
            Status = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            Searching = false;
        }
        // Once per visit, so Search again doesn't keep throwing it up.
        if (blocked && !_firewallHelpShown && ShowNetworkHelp is { } show)
        {
            _firewallHelpShown = true;
            await show();
        }
    }

    // Windows drops the radio's packets until a rule names the engine. The
    // engine asks for it once on first start; if that was declined it never
    // asks again, and all anyone sees is an empty list.
    private async Task<bool> CheckFirewallAsync()
    {
        FirewallBlocked = false;
        NoRadios = Radios.Count == 0;
        if (Radios.Count > 0) return false;
        try
        {
            var firewall = await _client.GetFirewallAsync();
            if (firewall.Missing)
            {
                FirewallBlocked = true;
                Status = "No radios found. Windows Firewall may be blocking the radio.";
            }
            else if (firewall.OwnedElsewhere)
                Status = "No radios found. Windows Firewall may be blocking the radio.";
            else
                return false;
            return true;
        }
        catch (Exception) { }
        return false;
    }

    [RelayCommand]
    private Task OpenNetworkHelpAsync() => ShowNetworkHelp?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private async Task AllowFirewallAsync()
    {
        Status = "Waiting for Windows to approve the firewall rule…";
        try
        {
            await _client.AllowFirewallAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = $"The firewall rule wasn't added: {ex.Message}";
        }
    }

    private bool CanConnect() =>
        Selected is { Busy: false } || System.Net.IPAddress.TryParse(ManualIp.Trim(), out _);

    /// <summary>Tell the engine which radio it is. False for "Other radio".</summary>
    private async Task<bool> SaveModelAsync()
    {
        if (ModelIndex < 0 || ModelIndex >= RadioModels.Names.Length) return false;
        if (ModelIndex != _engineModel)
            await _client.SetRadioVariantAsync((OrionMkIIVariant)ModelIndex);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        try
        {
            if (Manual)
            {
                bool orion = await SaveModelAsync();
                Done?.Invoke(new RadioChoice(ManualIp.Trim(), ManualP2, orion ? RadioModels.OrionBoardId : null, null, ManualIp.Trim()));
            }
            else if (Selected is { Busy: false } s)
            {
                if (ShowModel) await SaveModelAsync();
                Done?.Invoke(s.ToChoice());
            }
        }
        catch (Exception ex)
        {
            Status = $"Could not set the radio model: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedBusy))]
    private async Task TakeOverAsync()
    {
        if (Selected is not { Busy: true } s) return;
        try
        {
            if (ShowModel) await SaveModelAsync();
            Done?.Invoke(s.ToChoice() with { TakeOver = true });
        }
        catch (Exception ex)
        {
            Status = $"Could not set the radio model: {ex.Message}";
        }
    }

    private bool CanFirmware() => Selected is { Protocol2: true, Busy: false };

    /// <summary>Protocol 2 radios only, and not one another program is using.</summary>
    [RelayCommand(CanExecute = nameof(CanFirmware))]
    private async Task FirmwareAsync()
    {
        if (Selected is { } s && ShowFirmware is { } show) await show(new FirmwareViewModel(s));
    }

    [RelayCommand]
    private void Cancel() => Done?.Invoke(null);
}
