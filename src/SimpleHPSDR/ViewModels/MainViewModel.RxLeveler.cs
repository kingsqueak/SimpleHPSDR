using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>Right-click settings for the RX Lev button. In Auto the engine
/// uses its own fixed values and ignores these; Custom uses them. The engine
/// keeps them, and takes the on state and the settings together.</summary>
public sealed partial class MainViewModel
{
    private Latest<int>? _rxLevSender;
    private int _rxLevSeq;
    private bool _rxLevLoading;

    /// <summary>0 is Auto, 1 is Custom.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RxLevCustom))] private int _rxLevModeIndex;
    [ObservableProperty] private double _rxLevTargetDb = RxLevelerConfig.DefaultTargetRmsDb;
    [ObservableProperty] private double _rxLevMaxBoostDb = RxLevelerConfig.DefaultMaxBoostDb;
    [ObservableProperty] private double _rxLevAttackMs = RxLevelerConfig.DefaultAttackMs;
    [ObservableProperty] private double _rxLevReleaseMs = RxLevelerConfig.DefaultReleaseMs;
    [ObservableProperty] private double _rxLevHangMs = RxLevelerConfig.DefaultHangMs;

    public bool RxLevCustom => RxLevModeIndex == 1;

    partial void OnRxLevModeIndexChanged(int value) => SendRxLeveler();
    partial void OnRxLevTargetDbChanged(double value) => SendRxLeveler();
    partial void OnRxLevMaxBoostDbChanged(double value) => SendRxLeveler();
    partial void OnRxLevAttackMsChanged(double value) => SendRxLeveler();
    partial void OnRxLevReleaseMsChanged(double value) => SendRxLeveler();
    partial void OnRxLevHangMsChanged(double value) => SendRxLeveler();

    /// <summary>The popup shows what the engine has when it opens.</summary>
    public Task LoadRxLevelerAsync() => !IsConnected ? Task.CompletedTask : Try(async () =>
        ShowRxLeveler((await _client!.GetStateAsync()).RxLeveler ?? new RxLevelerConfig()));

    private void ShowRxLeveler(RxLevelerConfig c)
    {
        _rxLevLoading = true;
        RxLevModeIndex = c.Mode == RxLevelerMode.Custom ? 1 : 0;
        RxLevTargetDb = c.TargetRmsDb;
        RxLevMaxBoostDb = c.MaxBoostDb;
        RxLevAttackMs = c.AttackMs;
        RxLevReleaseMs = c.ReleaseMs;
        RxLevHangMs = c.HangMs;
        _rxLevLoading = false;
    }

    private void SendRxLeveler()
    {
        if (_rxLevLoading || !IsConnected) return;
        _rxLevSender ??= new Latest<int>(_ => _client!.SetRxLevelerConfigAsync(RxLevelerOn, new RxLevelerConfig(
            RxLevCustom ? RxLevelerMode.Custom : RxLevelerMode.Auto,
            RxLevTargetDb, RxLevMaxBoostDb,
            (int)Math.Round(RxLevAttackMs), (int)Math.Round(RxLevReleaseMs), (int)Math.Round(RxLevHangMs))));
        _rxLevSender.Send(++_rxLevSeq);
    }

    [RelayCommand]
    private void ResetRxLeveler()
    {
        ShowRxLeveler(new RxLevelerConfig());
        SendRxLeveler();
    }
}
