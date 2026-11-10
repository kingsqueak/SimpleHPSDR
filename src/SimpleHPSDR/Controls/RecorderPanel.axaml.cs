using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Controls;

public partial class RecorderPanel : UserControl
{
    public RecorderPanel() => InitializeComponent();

    // The flyout's content is attached each time it opens: show the folder as it is now.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is MainViewModel vm) _ = vm.RefreshRecordingsAsync();
    }

    private void OnLabelLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: RecordingRow row }) row.CommitLabel();
    }

    private void OnLabelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not Control { DataContext: RecordingRow row }) return;
        row.CommitLabel();
        e.Handled = true;
    }
}
