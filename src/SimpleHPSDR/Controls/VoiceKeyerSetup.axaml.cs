using Avalonia;
using Avalonia.Controls;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Controls;

public partial class VoiceKeyerSetup : UserControl
{
    public VoiceKeyerSetup() => InitializeComponent();

    // Attached each time the flyout opens: the current recordings for the picker.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as VoiceKeyerSlot)?.Prepare();
    }
}
