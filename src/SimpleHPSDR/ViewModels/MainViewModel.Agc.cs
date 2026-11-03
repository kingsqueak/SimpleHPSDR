using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>AGC: the button turns the engine's auto AGC on (blue) or off;
/// off is manual, where the speed (Off/Long/Slow/Med/Fast) is chosen; Off
/// is the engine's Fixed mode, a fixed gain. Both slices get the same AGC.</summary>
public sealed partial class MainViewModel
{
    private static readonly AgcMode[] AgcSpeeds = [AgcMode.Fixed, AgcMode.Long, AgcMode.Slow, AgcMode.Med, AgcMode.Fast];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AgcManual))] [NotifyPropertyChangedFor(nameof(AgcSubLabel))]
    private bool _agcAuto;
    [ObservableProperty] private string _agcSpeedLabel = "Med";
    private AgcMode _agcMode = AgcMode.Med;

    public bool AgcManual => !AgcAuto;
    private static string SpeedName(AgcMode m) => m == AgcMode.Fixed ? "Off" : m.ToString();
    public string AgcSubLabel => AgcAuto ? "auto" : "manual";
    public ObservableCollection<Choice> AgcSpeedChoices { get; } = [];

    /// <summary>From engine state (connect and the 1 s poll).</summary>
    private void UpdateAgc(StateDto s)
    {
        if (AgcSpeedChoices.Count == 0)
            foreach (var m in AgcSpeeds)
                AgcSpeedChoices.Add(new Choice(SpeedName(m), m, c => _ = SelectAgcSpeedAsync((AgcMode)c.Value)));
        AgcAuto = s.AutoAgcEnabled;
        _agcMode = s.Agc?.Mode ?? AgcMode.Med;
        AgcSpeedLabel = SpeedName(_agcMode);
        Mark(AgcSpeedChoices, _agcMode);
    }

    [RelayCommand]
    private async Task ToggleAgcAutoAsync()
    {
        if (!IsConnected) return;
        bool on = !AgcAuto;
        await Try(async () =>
        {
            await _client!.SetAutoAgcAsync(on);
            if (SliceBEnabled) await _client.SetReceiverAsync(1, new ReceiverSetRequest(AutoAgcEnabled: on));
            AgcAuto = on;
            Status = on ? "AGC auto: the engine sets the AGC gain." : $"AGC manual, speed {SpeedName(_agcMode)}.";
        });
    }

    private async Task SelectAgcSpeedAsync(AgcMode mode)
    {
        if (!IsConnected) return;
        await Try(async () =>
        {
            var agc = new AgcConfig(mode);
            await _client!.SetAgcAsync(agc);
            if (SliceBEnabled) await _client.SetReceiverAsync(1, new ReceiverSetRequest(Agc: agc));
            _agcMode = mode;
            AgcSpeedLabel = SpeedName(mode);
            Mark(AgcSpeedChoices, mode);
        });
    }
}
