using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace SimpleHPSDR.Controls;

/// <summary>
/// <c>c:ContextCommand.Command="{Binding …}"</c> runs a command on right-click,
/// or on a long press (LongPress raises the same context request), instead of
/// opening a menu. For buttons in shared templates, which have no code-behind.
/// </summary>
public static class ContextCommand
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(ContextCommand));

    public static ICommand? GetCommand(Control c) => c.GetValue(CommandProperty);
    public static void SetCommand(Control c, ICommand? value) => c.SetValue(CommandProperty, value);

    static ContextCommand()
    {
        CommandProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.ContextRequested -= OnContext;
            if (e.NewValue is not null) c.ContextRequested += OnContext;
        });
    }

    private static void OnContext(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control c && GetCommand(c) is { } cmd && cmd.CanExecute(null))
        {
            cmd.Execute(null);
            e.Handled = true;
        }
    }
}
