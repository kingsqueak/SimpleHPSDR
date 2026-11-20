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
        if (!vm.ShowPanadapterB) items.Add(Item(MainViewModel.TwoReceiversOnly ? "Turn RX2 on (second panadapter)" : "Turn RX2 on (second panadapter, slices B1/B2)", () => vm.SelectRxAsync(1)));
        else
        {
            items.Add(SideBySideItem(vm));
            items.Add(Item("Swap RX1 ⇄ RX2 pans", SwapPansAsync));
            items.Add(Item("Turn RX2 off", () => vm.RemovePanBAsync()));
        }
        new ContextMenu { ItemsSource = items }.Open(at);

        // Exchange the pans' primary slices' frequencies: tuning each makes
        // it active as it goes, which is acceptable for a menu action.
        async Task SwapPansAsync()
        {
            int first = MainViewModel.PanPrimary(0), second = MainViewModel.PanPrimary(1);
            long a = vm.RxHz(first), b = vm.RxHz(second);
            await vm.TuneSliceAsync(first, b);
            await vm.TuneSliceAsync(second, a);
        }
    }

    private static MenuItem SideBySideItem(MainViewModel vm) => vm.PanadaptersSideBySide
        ? Item("Panadapters one above the other", () => { vm.PanadaptersSideBySide = false; return Task.CompletedTask; })
        : Item("Panadapters side by side", () => { vm.PanadaptersSideBySide = true; return Task.CompletedTask; });

    // Right-click on a panadapter. With one slice: add the next one (A2) here,
    // or turn RX2's panadapter on. With more: make a slice active, move
    // transmit, mute, add the next slice, and turn the top slice — or RX2
    // itself — off. The second panadapter opens from the RX2 button; the touch
    // layout has that button in its drawer, so there the menu offers RX2 as well.
    public static void ShowSliceMenu(MainViewModel? vm, long hz, Control at, bool offerRx2 = false)
    {
        if (vm is not { IsConnected: true }) return;
        string where = $"{hz / 1e6:0.000000} MHz";
        var items = new List<Control>();

        if (vm.SliceCount < 2)
        {
            if (!MainViewModel.TwoReceiversOnly) items.Add(Item($"Add slice A2 at {where}", () => vm.AddSliceAsync(hz)));
            if (offerRx2 && !vm.ShowPanadapterB)
                items.Add(Item("Turn RX2 on (second panadapter)", () => vm.SelectRxAsync(1)));
        }
        else
        {
            for (int i = 0; i < vm.SliceCount; i++)
            {
                int s = i;
                string label = MainViewModel.SliceLabel(s);
                if (s != vm.ActiveRx) items.Add(Item($"Make {label} active", () => vm.MakeActiveAsync(s)));
                if (vm.TxRx != s) items.Add(Item($"Transmit on {label}", () => vm.TransmitOnAsync(s)));
            }
            items.Add(new Separator());
            for (int i = 0; i < vm.SliceCount; i++)
            {
                int s = i;
                string label = MainViewModel.SliceLabel(s);
                bool muted = vm.IsSliceMuted(s);
                items.Add(Item(muted ? $"Unmute {label}" : $"Mute {label}", () => vm.SetSliceMuteAsync(s, !muted)));
            }
            items.Add(new Separator());
            if (vm.CanAddSlice) items.Add(Item($"Add slice {vm.NextSliceLabel} at {where}", () => vm.AddSliceAsync(hz)));
            int top = vm.SliceCount - 1;
            if (top > 0) items.Add(Item($"Turn {MainViewModel.SliceLabel(top)} off", () => vm.RemoveSliceAsync(top)));
            if (vm.ShowPanadapterB)
            {
                items.Add(SideBySideItem(vm));
                items.Add(Item("Turn RX2 off", () => vm.RemovePanBAsync()));
            }
            else if (offerRx2 && vm.CanAddSlice)
                items.Add(Item("Turn RX2 on (second panadapter)", () => vm.SelectRxAsync(1)));
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
