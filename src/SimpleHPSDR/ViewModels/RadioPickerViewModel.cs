using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

public sealed record RadioChoice(string Ip, bool Protocol2, byte? BoardId, string? Mac, string Label, bool TakeOver = false);

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
    private RadioEntry? _selected;
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _manualIp = "";
    [ObservableProperty] private bool _manualP2 = true;
    [ObservableProperty] private bool _autoConnect;

    public ObservableCollection<RadioEntry> Radios { get; } = [];
    public bool SelectedBusy => Selected is { Busy: true };
    public event Action<RadioChoice?>? Done;

    public RadioPickerViewModel(StationClient client, bool autoConnect)
    {
        _client = client;
        _autoConnect = autoConnect;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        Searching = true;
        Status = "Searching the network…";
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
        }
        catch (Exception ex)
        {
            Status = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            Searching = false;
        }
    }

    private bool CanConnect() =>
        Selected is { Busy: false } || System.Net.IPAddress.TryParse(ManualIp.Trim(), out _);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        if (System.Net.IPAddress.TryParse(ManualIp.Trim(), out _))
            Done?.Invoke(new RadioChoice(ManualIp.Trim(), ManualP2, null, null, ManualIp.Trim()));
        else if (Selected is { Busy: false } s)
            Done?.Invoke(s.ToChoice());
    }

    [RelayCommand(CanExecute = nameof(SelectedBusy))]
    private void TakeOver()
    {
        if (Selected is { Busy: true } s)
            Done?.Invoke(s.ToChoice() with { TakeOver = true });
    }

    [RelayCommand]
    private void Cancel() => Done?.Invoke(null);
}
