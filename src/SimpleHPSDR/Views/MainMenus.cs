using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Views;

/// <summary>Menus built in code, shared by the main window's layouts.</summary>
public static class MainMenus
{
    private static MenuItem Item(string header, Func<Task> act) =>
        new() { Header = header, Command = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(act) };

    // Right-click on the band: frequencies saved on this band.
    public static async Task ShowBandStackAsync(MainViewModel? vm, Control at)
    {
        if (vm is not { IsConnected: true }) return;
        try { await BuildBandStackMenuAsync(vm, at); }
        catch (Exception ex) { vm.ShowProblem($"Could not read the band stack: {ex.Message}"); }
    }

    private static async Task BuildBandStackMenuAsync(MainViewModel vm, Control at)
    {
        var items = new List<Control> { Item($"Save {vm.FrequencyText} {vm.ModeLabel}", vm.SaveToBandStackAsync) };
        var saved = await vm.BandStackAsync();
        foreach (var e in saved)
            items.Add(Item(e.Label, () => vm.RecallBandStackAsync(e)));
        if (saved.Count > 0)
        {
            items.Add(new Separator());
            items.Add(new MenuItem
            {
                Header = "Remove",
                ItemsSource = saved.Select(e => Item(e.Label, () => vm.RemoveBandStackAsync(e))).ToList(),
            });
        }
        new ContextMenu { ItemsSource = items }.Open(at);
    }

    // Right-click on the panadapter: add, select, move, mute, remove slices and
    // choose which one transmits.
    public static void ShowSliceMenu(MainViewModel? vm, long hz, Control at)
    {
        if (vm is not { IsConnected: true }) return;
        string where = $"{hz / 1e6:0.000000} MHz";
        var items = new List<Control>();

        if (!vm.SliceBEnabled)
        {
            items.Add(Item($"Add slice B at {where}", () => vm.AddSliceBAsync(hz, ownPanadapter: false)));
            items.Add(Item($"Add slice B at {where} in a new panadapter", () => vm.AddSliceBAsync(hz, ownPanadapter: true)));
        }
        else
        {
            int other = vm.ActiveRx == 0 ? 1 : 0;
            string o = other == 0 ? "A" : "B";
            items.Add(Item($"Make slice {o} active", () => vm.MakeActiveAsync(other)));
            items.Add(Item($"Move slice B here ({where})", () => vm.MoveSliceBAsync(hz)));
            items.Add(new Separator());
            for (int rx = 0; rx <= 1; rx++)
            {
                int r = rx;
                string n = r == 0 ? "A" : "B";
                if (vm.TxRx != r) items.Add(Item($"Transmit on slice {n}", () => vm.TransmitOnAsync(r)));
            }
            items.Add(new Separator());
            for (int rx = 0; rx <= 1; rx++)
            {
                int r = rx;
                string n = r == 0 ? "A" : "B";
                bool muted = vm.IsSliceMuted(r);
                items.Add(Item(muted ? $"Unmute slice {n}" : $"Mute slice {n}", () => vm.SetSliceMuteAsync(r, !muted)));
            }
            items.Add(new Separator());
            items.Add(vm.SliceBOwnPanadapter
                ? Item("Show slice B on this panadapter", () => { vm.SliceBOwnPanadapter = false; return Task.CompletedTask; })
                : Item("Show slice B in its own panadapter", () => { vm.SliceBOwnPanadapter = true; return Task.CompletedTask; }));
            if (vm.SliceBOwnPanadapter)
                items.Add(vm.PanadaptersSideBySide
                    ? Item("Panadapters one above the other", () => { vm.PanadaptersSideBySide = false; return Task.CompletedTask; })
                    : Item("Panadapters side by side", () => { vm.PanadaptersSideBySide = true; return Task.CompletedTask; }));
            items.Add(Item("Swap A and B", () => { vm.SwapSlicesCommand.Execute(null); return Task.CompletedTask; }));
            items.Add(Item("Remove slice B", () => vm.RemoveSliceBAsync()));
        }
        new ContextMenu { ItemsSource = items }.Open(at);
    }

    // A button's Click runs before its Command; closing the popup right away
    // would detach the button and the command would never run, so close
    // once the click has finished.
    public static void CloseFlyout(object? sender)
    {
        var popup = (sender as Control)?.FindLogicalAncestorOfType<Popup>();
        Dispatcher.UIThread.Post(() => popup?.Close());
    }
}
