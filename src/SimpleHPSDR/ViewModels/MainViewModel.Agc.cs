using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zeus.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>AGC: the button turns the engine's auto AGC on (blue) or off;
/// off is manual, where the speed (Long/Slow/Med/Fast) is chosen. Both
/// slices get the same AGC.</summary>
public sealed partial class MainViewModel
{
    private static readonly AgcMode[] AgcSpeeds = [AgcMode.Long, AgcMode.Slow, AgcMode.Med, AgcMode.Fast];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AgcManual))] [NotifyPropertyChangedFor(nameof(AgcSubLabel))]
    private bool _agcAuto;
    [ObservableProperty] private string _agcSpeedLabel = "Med";
    private AgcMode _agcMode = AgcMode.Med;

    public bool AgcManual => !AgcAuto;
    public string AgcSubLabel => AgcAuto ? "auto" : "manual";
    public ObservableCollection<Choice> AgcSpeedChoices { get; } = [];

    /// <summary>From engine state (connect and the 1 s poll).</summary>
    private void UpdateAgc(StateDto s)
    {
        if (AgcSpeedChoices.Count == 0)
            foreach (var m in AgcSpeeds)
                AgcSpeedChoices.Add(new Choice(m.ToString(), m, c => _ = SelectAgcSpeedAsync((AgcMode)c.Value)));
        AgcAuto = s.AutoAgcEnabled;
        _agcMode = s.Agc?.Mode ?? AgcMode.Med;
        AgcSpeedLabel = _agcMode.ToString();
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
            Status = on ? "AGC auto: the engine sets the AGC gain." : $"AGC manual, speed {_agcMode}.";
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
            AgcSpeedLabel = mode.ToString();
            Mark(AgcSpeedChoices, mode);
        });
    }
}
