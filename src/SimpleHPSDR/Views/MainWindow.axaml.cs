using Avalonia.Controls;
using Avalonia.Input;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FrequencyPanel.PointerWheelChanged += (_, e) =>
        {
            Vm?.TuneBySteps(Math.Sign(e.Delta.Y));
            e.Handled = true;
        };
        FrequencyBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Vm?.EnterFrequencyCommand.Execute(null);
        };
        Spectrum.TuneRequested += hz => Vm?.TuneTo(hz);
        Spectrum.WheelTune += steps => Vm?.TuneBySteps(steps);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        vm.ShowRadioPicker = picker => new RadioPickerWindow { DataContext = picker }.PickAsync(this);
        vm.ShowSettings = settings => new SettingsWindow { DataContext = settings }.ShowDialog(this);
        vm.Display += f =>
        {
            Spectrum.VfoHz = vm.VfoHz;
            Spectrum.FilterLowHz = vm.FilterLowHz;
            Spectrum.FilterHighHz = vm.FilterHighHz;
            Spectrum.PushFrame(f);
        };
        vm.DisplayReset += Spectrum.Clear;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Vm is { } vm) await vm.StartAsync();
    }
}
