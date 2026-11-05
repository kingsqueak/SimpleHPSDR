using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleHPSDR.Engine;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// Automatic PA calibration: the engine keys TUNE on every HF band and sets
/// each band's PA gain from the radio's own power meter. It owns the keying and
/// the safety stops; this only starts it, shows it and takes the operator's
/// decision about a partial result. It starts only from the window's button.
/// </summary>
public sealed partial class PaCalViewModel(StationClient client) : ObservableObject
{
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _dummyLoadConfirmed;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _amplifierOffConfirmed;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Idle), nameof(CanClose))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _running;
    [ObservableProperty] private bool _stopping;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Idle))] private bool _finished;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanClose))] private bool _pendingSave;
    [ObservableProperty] private string _bandText = "";
    [ObservableProperty] private string _powerText = "";
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _failuresText = "";
    [ObservableProperty] private string _errorText = "";

    /// <summary>The confirmation page is showing.</summary>
    public bool Idle => !Running && !Finished;
    /// <summary>Held results have to be decided first; a run can't be left behind.</summary>
    public bool CanClose => !Running && !PendingSave;

    /// <summary>Pick up a run or held results from before this window opened.</summary>
    public async Task OpenAsync()
    {
        try
        {
            var s = await client.GetPaCalibrationAsync();
            if (s.State is "running" or "cancelling" || s.PendingSave) await FollowAsync(s);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
    }

    private bool CanStart() => DummyLoadConfirmed && AmplifierOffConfirmed && !Running;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ErrorText = "";
        try
        {
            await client.StartPaCalibrationAsync(AmplifierOffConfirmed);
            await FollowAsync(await client.GetPaCalibrationAsync());
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;   // the engine's reason for refusing
            if (ex.Message.Contains("band plan", StringComparison.OrdinalIgnoreCase))
                ErrorText += " Choose a plan with every HF band in Config > Station.";
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        Stopping = true;
        try { await client.CancelPaCalibrationAsync(); }
        catch (Exception ex) { MessageText = $"Could not ask it to stop: {ex.Message}"; }
    }

    [RelayCommand]
    private Task SaveAsync() => DecideAsync(client.SavePaCalibrationAsync, "Saved the bands that passed. The others are unchanged.");

    [RelayCommand]
    private Task DiscardAsync() => DecideAsync(client.DiscardPaCalibrationAsync, "Discarded. Every band keeps its previous PA gain.");

    private async Task DecideAsync(Func<Task> decide, string done)
    {
        try
        {
            await decide();
            PendingSave = false;
            ResultText = done;
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }

    private async Task FollowAsync(PaCalStatus status)
    {
        Running = true;
        var s = status;
        int missed = 0;
        while (s.State is "running" or "cancelling")
        {
            Show(s);
            await Task.Delay(250);
            try
            {
                s = await client.GetPaCalibrationAsync();
                missed = 0;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // No engine means no transmit; carry on only through a brief hiccup.
                if (++missed < 8) continue;
                try { await client.CancelPaCalibrationAsync(); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StationException) { }
                Running = false;
                Finished = true;
                ResultText = "Lost contact with the engine during calibration.";
                return;
            }
        }
        Show(s);
        Running = false;
        Stopping = false;
        Finished = true;
        PendingSave = s.PendingSave;
        ResultText = s.Message ?? s.State;
        FailuresText = string.Join("\n", (s.FailedBands ?? []).Select(f => $"{f.Band}: {f.Reason}"));
    }

    private void Show(PaCalStatus s)
    {
        Percent = s.TotalSteps > 0 ? 100.0 * s.CompletedSteps / s.TotalSteps : 0;
        BandText = s.Band ?? "";
        PowerText = (s.TargetWatts, s.MeasuredWatts) switch
        {
            ({ } t, { } m) => $"Target {t:0} W, measured {m:0.0} W",
            ({ } t, null) => $"Target {t:0} W",
            _ => "",
        };
        MessageText = s.Message ?? "";
    }
}
