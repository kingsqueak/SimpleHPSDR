using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.ViewModels;

/// <summary>One slider of the receive EQ.</summary>
public sealed partial class RxEqBand(MainViewModel owner, double hz, double gainDb) : ObservableObject
{
    public double Hz { get; } = hz;
    public string Label => Hz >= 1000 ? $"{Hz / 1000:0.#}k" : $"{Hz:0}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    private double _gainDb = gainDb;

    public string GainText => GainDb > 0.05 ? $"+{GainDb:0}" : $"{GainDb:0}";

    partial void OnGainDbChanged(double value) => owner.SendRxEq();
}

/// <summary>
/// Receive EQ: ten points through the engine's RX EQ, the same on RX1 and RX2,
/// kept by the engine. Click AF to turn it on or off, right-click (long press)
/// for the sliders. The engine leaves it out in DIGU/DIGL.
/// </summary>
public sealed partial class MainViewModel
{
    public const double RxEqLimitDb = 20;

    [ObservableProperty] private bool _rxEqOn;
    public ObservableCollection<RxEqBand> RxEqBands { get; } = [];

    private RxEqConfig _rxEq = RxEqConfig.Default;
    private Latest<int>? _rxEqSender;
    private int _rxEqSends;
    private bool _applyingRxEq;
    // A poll that left before a change could bring back the old curve.
    private DateTime _rxEqChangedUtc;

    public string RxEqTip => "AF: click to turn the receive EQ on or off (blue = on). Right-click to shape it.";

    private void UpdateRxEq(StateDto s)
    {
        if (_rxEqSender is { Busy: true } || DateTime.UtcNow - _rxEqChangedUtc < TimeSpan.FromSeconds(2)) return;
        var eq = s.RxEq ?? RxEqConfig.Default;
        _applyingRxEq = true;
        try
        {
            RxEqOn = eq.Enabled;
            bool sameBands = RxEqBands.Count == eq.FrequenciesHz.Length
                && RxEqBands.Select(b => b.Hz).SequenceEqual(eq.FrequenciesHz);
            if (!sameBands)
            {
                RxEqBands.Clear();
                for (int i = 0; i < eq.FrequenciesHz.Length; i++)
                    RxEqBands.Add(new RxEqBand(this, eq.FrequenciesHz[i], Math.Clamp(eq.GainsDb[i], -RxEqLimitDb, RxEqLimitDb)));
            }
            else
            {
                for (int i = 0; i < RxEqBands.Count; i++)
                    RxEqBands[i].GainDb = Math.Clamp(eq.GainsDb[i], -RxEqLimitDb, RxEqLimitDb);
            }
            _rxEq = eq;
        }
        finally
        {
            _applyingRxEq = false;
        }
    }

    partial void OnRxEqOnChanged(bool value) => SendRxEq();

    [RelayCommand]
    private void ToggleRxEq() => RxEqOn = !RxEqOn;

    [RelayCommand]
    private void FlattenRxEq()
    {
        _applyingRxEq = true;
        foreach (var b in RxEqBands) b.GainDb = 0;
        _applyingRxEq = false;
        SendRxEq();
    }

    internal void SendRxEq()
    {
        if (_applyingRxEq || !IsConnected || RxEqBands.Count != 10) return;
        _rxEqChangedUtc = DateTime.UtcNow;
        _rxEq = new RxEqConfig(RxEqOn, 0,
            [.. RxEqBands.Select(b => b.Hz)],
            [.. RxEqBands.Select(b => Math.Round(b.GainDb, 1))]);
        _rxEqSender ??= new Latest<int>(_ => _client!.SetRxEqAsync(_rxEq));
        _rxEqSender.Send(++_rxEqSends);
    }
}
