using Avalonia.Controls;

namespace SimpleHPSDR.Views;

/// <summary>First start: the user picks a band plan. Closing the window
/// keeps the one preselected.</summary>
public partial class FirstStartWindow : Window
{
    public FirstStartWindow()
    {
        InitializeComponent();
        ContinueButton.Click += (_, _) => Close();
    }

    /// <summary>The index chosen from the plan names.</summary>
    public async Task<int> ChooseAsync(Window owner, IReadOnlyList<string> plans, int preselected)
    {
        Plans.ItemsSource = plans;
        Plans.SelectedIndex = preselected;
        await ShowDialog(owner);
        return Plans.SelectedIndex;
    }
}
