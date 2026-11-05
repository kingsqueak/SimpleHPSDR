using Avalonia.Controls;

namespace SimpleHPSDR.Views;

/// <summary>The engine died by itself. True if the user asks to restart it;
/// nothing restarts without that, so a fault can't turn into a loop.</summary>
public partial class EngineStoppedWindow : Window
{
    public EngineStoppedWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close(false);
        RestartButton.Click += (_, _) => Close(true);
    }

    public Task<bool> AskAsync(Window owner, string radio, string reason, string logPath)
    {
        Body.Text = $"SimpleHPSDR has lost {radio}. Nothing is received or transmitted until the engine restarts. "
                  + "Restarting reconnects with transmit off.";
        Reason.Text = reason;
        LogPath.Text = logPath;
        return ShowDialog<bool>(owner);
    }
}
