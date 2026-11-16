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

    // Right-click on the RX2 button.
    public static void ShowRx2Menu(MainViewModel? vm, Control at)
    {
        if (vm is not { IsConnected: true }) return;
        var items = new List<Control>();
        if (!vm.ShowPanadapterB) items.Add(Item("Turn RX2 on", () => vm.SelectRxAsync(1)));
        else
        {
            items.Add(SideBySideItem(vm));
            items.Add(Item("Swap RX1 and RX2", () => { vm.SwapSlicesCommand.Execute(null); return Task.CompletedTask; }));
            items.Add(Item("Turn RX2 off", () => vm.RemoveSliceBAsync()));
        }
        new ContextMenu { ItemsSource = items }.Open(at);
    }

    private static MenuItem SideBySideItem(MainViewModel vm) => vm.PanadaptersSideBySide
        ? Item("Panadapters one above the other", () => { vm.PanadaptersSideBySide = false; return Task.CompletedTask; })
        : Item("Panadapters side by side", () => { vm.PanadaptersSideBySide = true; return Task.CompletedTask; });

    // Right-click on a panadapter. With RX2 on: which receiver has the
    // controls, which transmits, mute, the arrangement, RX2 off. With one
    // panadapter: add, select, move, mute and remove slice B the same way.
    // The second panadapter itself opens from the RX2 button.
    public static void ShowSliceMenu(MainViewModel? vm, long hz, Control at)
    {
        if (vm is not { IsConnected: true }) return;
        string where = $"{hz / 1e6:0.000000} MHz";
        var items = new List<Control>();

        if (!vm.SliceBEnabled)
        {
            items.Add(Item($"Add slice B at {where}", () => vm.AddSliceBAsync(hz, ownPanadapter: false)));
        }
        else
        {
            bool two = vm.SliceBOwnPanadapter;
            int other = vm.ActiveRx == 0 ? 1 : 0;
            items.Add(Item($"Make {vm.SliceName(other)} active", () => vm.MakeActiveAsync(other)));
            items.Add(Item($"Move {vm.SliceName(1)} here ({where})", () => vm.MoveSliceBAsync(hz)));
            items.Add(new Separator());
            for (int rx = 0; rx <= 1; rx++)
            {
                int r = rx;
                if (vm.TxRx != r && !(r == 1 && vm.Rx2ReceiveOnly)) items.Add(Item($"Transmit on {vm.SliceName(r)}", () => vm.TransmitOnAsync(r)));
            }
            items.Add(new Separator());
            for (int rx = 0; rx <= 1; rx++)
            {
                int r = rx;
                string n = vm.SliceName(r);
                bool muted = vm.IsSliceMuted(r);
                items.Add(Item(muted ? $"Unmute {n}" : $"Mute {n}", () => vm.SetSliceMuteAsync(r, !muted)));
            }
            items.Add(new Separator());
            if (two) items.Add(SideBySideItem(vm));
            items.Add(Item(two ? "Swap RX1 and RX2" : "Swap A and B", () => { vm.SwapSlicesCommand.Execute(null); return Task.CompletedTask; }));
            items.Add(Item(two ? "Turn RX2 off" : "Remove slice B", () => vm.RemoveSliceBAsync()));
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
