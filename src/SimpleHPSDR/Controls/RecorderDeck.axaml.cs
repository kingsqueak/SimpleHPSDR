using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Controls;

public partial class RecorderDeck : UserControl
{
    public RecorderDeck() => InitializeComponent();

    private void Commit()
    {
        if (DataContext is MainViewModel { DeckSelected: { } row }) row.CommitLabel();
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Commit();
        e.Handled = true;
    }

    // Choosing one closes the list. Deleting one doesn't: the list stays open
    // for the next, and never closes from inside its own button's click.
    private void OnPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel { DeckDeleting: true }) return;
        if (e.AddedItems.Count > 0) ListButton.Flyout?.Hide();
    }
}
