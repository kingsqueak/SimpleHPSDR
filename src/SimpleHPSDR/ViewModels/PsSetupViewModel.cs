using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimpleHPSDR.ViewModels;

/// <summary>
/// Guided PureSignal setup: where the feedback comes from, what to check
/// before transmitting, then PS CAL with the result in plain words. It
/// starts from the radio's default HW peak, so a value left over from
/// earlier tries can't stop it locking.
/// </summary>
public sealed partial class PsSetupViewModel : ObservableObject
{
    public MainViewModel Main { get; }

    public PsSetupViewModel(MainViewModel main, bool external)
    {
        Main = main;
        _external = external;
        main.PropertyChanged += OnMainChanged;
    }

    /// <summary>0: feedback source, 1: checks, 2: calibrating and the result.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnSource), nameof(OnChecks), nameof(OnRun))]
    private int _step;
    public bool OnSource => Step == 0;
    public bool OnChecks => Step == 1;
    public bool OnRun => Step == 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Internal), nameof(CanStart))]
    private bool _external;
    public bool Internal
    {
        get => !External;
        set { if (value) External = false; }
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart))] private bool _dummyLoad;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart))] private bool _ampReady;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart))] private bool _powerSet;
    public bool CanStart => DummyLoad && PowerSet && (!External || AmpReady);

    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _finished;
    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private string _resultText = "";

    /// <summary>The window closes; set once the operator is done with it.</summary>
    public event Action? CloseRequested;

    [RelayCommand] private void Next() => Step = Math.Min(2, Step + 1);
    [RelayCommand] private void Back() => Step = Math.Max(0, Step - 1);

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart || Running) return;
        Step = 2;
        Running = true;
        Finished = false;
        ResultText = "";
        try
        {
            Succeeded = await Main.RunGuidedPsCalAsync(External);
        }
        finally
        {
            Running = false;
            Finished = true;
        }
        ResultText = Succeeded
            ? "PureSignal is set up and locked. It keeps its correction while you talk. " +
              "Run PS CAL again (right-click PS) after changing band, power or amplifier. " +
              "To see how much cleaner it is, turn on 2-TONE (right-click PS) into the dummy load and leave it on about 15 seconds: " +
              "the PS panel shows the IMD before and after."
            : "It didn't lock. " + Main.PsAdvice;
        if (Succeeded) Main.MarkPsSetupDone();
    }

    /// <summary>Stop a calibration that's running.</summary>
    [RelayCommand]
    private void Stop()
    {
        if (Main.PsCalibrating) Main.CalibratePsCommand.Execute(null);
    }

    [RelayCommand]
    private void TryAgain()
    {
        Finished = false;
        Step = 1;
    }

    /// <summary>For people who know PureSignal: don't open this by itself again.</summary>
    [RelayCommand]
    private void Skip()
    {
        Main.MarkPsSetupDone();
        CloseRequested?.Invoke();
    }

    [RelayCommand] private void Close() => CloseRequested?.Invoke();

    public void Detach() => Main.PropertyChanged -= OnMainChanged;

    // The calibration's progress shows in the main window's status line.
    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Status)) OnPropertyChanged(nameof(Progress));
    }

    public string Progress => Main.Status;
}
