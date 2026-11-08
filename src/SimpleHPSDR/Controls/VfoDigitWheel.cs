using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SimpleHPSDR.ViewModels;

namespace SimpleHPSDR.Controls;

/// <summary>
/// Hover tuning on the VFO readout: the digit under the pointer lights up in
/// place of the step digit, and the wheel moves that digit (10 Hz, 1 kHz, ...).
/// Over the dots, or anywhere else on the panel, the wheel tunes by the step
/// as before.
/// </summary>
public static class VfoDigitWheel
{
    public static void Attach(TextBlock readout, Func<MainViewModel?> vm)
    {
        readout.PointerMoved += (_, e) =>
        {
            if (vm() is { } m) m.HoverDigitPlace = PlaceAt(readout, m, e.GetPosition(readout));
        };
        readout.PointerExited += (_, _) =>
        {
            if (vm() is { } m) m.HoverDigitPlace = -1;
        };
        readout.PointerWheelChanged += (_, e) =>
        {
            if (vm() is not { } m) return;
            int place = PlaceAt(readout, m, e.GetPosition(readout));
            if (place < 0) return;   // not on a digit: the panel's step tuning handles it
            m.HoverDigitPlace = place;
            int clicks = SliderWheel.Clicks(readout, e);
            if (clicks != 0) m.TuneDigit(place, clicks);
            e.Handled = true;
        };
    }

    /// <summary>The power of ten of the digit at a point in the readout, or -1.</summary>
    private static int PlaceAt(TextBlock readout, MainViewModel vm, Point at)
    {
        var layout = readout.TextLayout;
        if (layout is null) return -1;
        var hit = layout.HitTestPoint(at);
        if (!hit.IsInside) return -1;
        string text = vm.FrequencyText;
        int i = hit.CharacterHit.FirstCharacterIndex;   // the character itself, not the nearer edge
        if (i < 0 || i >= text.Length || !char.IsAsciiDigit(text[i])) return -1;
        int place = 0;
        for (int j = i + 1; j < text.Length; j++)
            if (char.IsAsciiDigit(text[j])) place++;
        return place;
    }
}
