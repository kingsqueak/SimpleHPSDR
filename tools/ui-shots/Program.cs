using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using SimpleHPSDR.ViewModels;
using SimpleHPSDR.Views;

// Never read or write the real settings: the app data folder must be under a
// throwaway home (on macOS .NET finds it via CFFIXED_USER_HOME, not HOME).
{
    var data = SimpleHPSDR.Engine.EngineHost.DataDir;
    var fake = Environment.GetEnvironmentVariable(OperatingSystem.IsMacOS() ? "CFFIXED_USER_HOME" : "HOME");
    var real = (OperatingSystem.IsMacOS() ? "/Users/" : "/home/") + Environment.UserName;
    if (string.IsNullOrEmpty(fake) || data.StartsWith(real + "/", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Refusing to run: app data folder is {data}. Set {(OperatingSystem.IsMacOS() ? "CFFIXED_USER_HOME" : "HOME")} to a throwaway folder (see README).");
        return 1;
    }
}
var outDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "out";
SimpleHPSDR.Platform.LayoutMode.ReadCommandLine(args);
Directory.CreateDirectory(outDir);
AppBuilder.Configure<SimpleHPSDR.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var report = new StringBuilder();
const double W = 1024, H = 535;

void Pump() { for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }

void Shot(Window w, string name)
{
    Pump();
    var frame = w.CaptureRenderedFrame();
    frame?.Save(Path.Combine(outDir, name + ".png"));
    Audit(w, name);
}

static string Label(Control c)
{
    string? s = c switch
    {
        ContentControl { Content: string t } => t,
        ContentControl cc => cc.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
        _ => null,
    };
    if (string.IsNullOrEmpty(s)) s = ToolTip.GetTip(c) as string;
    if (string.IsNullOrEmpty(s)) s = c.Name;
    s = (s ?? "?").Replace("\n", " ");
    return s.Length > 40 ? s[..40] + "…" : s;
}

void Audit(Window w, string name)
{
    var client = w.ClientSize;
    report.AppendLine($"## {name}  client {client.Width}x{client.Height}");
    var root = (Control)w.Content!;
    root.Measure(Size.Infinity);
    report.AppendLine($"content natural size {root.DesiredSize.Width:0}x{root.DesiredSize.Height:0}");
    var small = new List<string>(); var off = new List<string>();
    foreach (var c in w.GetVisualDescendants().OfType<Control>())
    {
        if (!c.IsEffectivelyVisible) continue;
        bool hit = c is Button or ToggleButton or CheckBox or ComboBox or Slider or TextBox or NumericUpDown or MenuItem or TabItem or RadioButton
                   || c.GetType().Namespace == "SimpleHPSDR.Controls";
        if (!hit) continue;
        if (c.Parent is Control p && (p is Button or ComboBox or NumericUpDown or Slider)) continue;
        var tb = c.GetTransformedBounds();
        if (tb is null) continue;
        var r = tb.Value.Bounds.TransformToAABB(tb.Value.Transform);
        var clip = tb.Value.Clip;
        string lbl = $"{c.GetType().Name} '{Label(c)}' {r.Width:0}x{r.Height:0} at {r.X:0},{r.Y:0}";
        if (r.Width < 1 || r.Height < 1) continue;
        bool clipped = clip.Width < r.Width - 1 || clip.Height < r.Height - 1 || r.Right > client.Width + 1 || r.Bottom > client.Height + 1;
        if (clipped) off.Add(lbl + $" (visible {clip.Width:0}x{clip.Height:0})");
        double min = c is Slider ? r.Height : Math.Min(r.Width, r.Height);
        if (c is Slider) { if (r.Height < 32) small.Add(lbl); }
        else if (min < 40) small.Add(lbl);
    }
    report.AppendLine($"### clipped or off-window ({off.Count})");
    foreach (var s in off) report.AppendLine("- " + s);
    report.AppendLine($"### under 40 px ({small.Count})");
    foreach (var s in small) report.AppendLine("- " + s);
    report.AppendLine();
}

Window Make(Window w, double width, double height)
{
    w.MinWidth = 0; w.MinHeight = 0;
    w.Width = width; w.Height = height;
    return w;
}

// ---- Main window ----
var vm = new MainViewModel();
void SetProp(object o, string p, object v)
{
    var pi = o.GetType().GetProperty(p, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    if (pi?.SetMethod != null) pi.SetValue(o, v);
    else
    {
        var f = o.GetType().GetField("_" + char.ToLower(p[0]) + p[1..], BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) f.SetValue(o, v); else Console.WriteLine($"no setter {p}");
    }
}
SetProp(vm, "IsConnected", true);
SetProp(vm, "RadioLabel", "ANAN-G2 Ultra · 192.168.16.2");
SetProp(vm, "Status", "Connected · RX");
SetProp(vm, "AntennaBoxesEnabled", true);
SetProp(vm, "VfoHz", 14_225_000L);
foreach (var (slot, lbl, lo, hi) in new[] { ("F1", "5.0k", 63, 5000), ("F2", "4.0k", 100, 4000), ("F3", "3.5k", 100, 3500), ("F4", "3.0k", 100, 3000), ("F5", "2.8k", 100, 2800) })
    vm.Filters.Add(new FilterSlot(Zeus.Contracts.RxMode.USB, new SimpleHPSDR.Engine.FilterPreset(slot, lbl, lo, hi), _ => Task.CompletedTask, _ => Task.CompletedTask, _ => Task.CompletedTask) { IsSelected = slot == "F2" });
void Notify(object o, string n) => o.GetType().GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!.Invoke(o, [n]);
var main = new MainWindow { DataContext = vm };
SimpleHPSDR.Platform.LayoutMode.Watch(main, vm.AppSettings);
typeof(MainWindow).GetField("_started", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, true);
var mainPrefix = SimpleHPSDR.Platform.LayoutMode.Override == "Touch" ? "touch-" : "";
foreach (var (w, h, n0) in new[] { (1200.0, 760.0, "main-1200x760-desktop"), (900.0, 532.0, "main-900x532"), (W, H, "main-1024x535"), (W, 600.0, "main-1024x600-fullscreen") })
{
    Make(main, w, h);
    if (!main.IsVisible) main.Show();
    Shot(main, mainPrefix + n0);
}
// Touch layout: the drawer on each tab
if (main.GetVisualDescendants().OfType<SplitView>().FirstOrDefault() is { } drawer)
{
    Make(main, W, 600);
    drawer.IsPaneOpen = true;
    // The pane slides open: let the animation run to the end.
    for (int t = 0; t < 120; t++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    foreach (var c in drawer.GetVisualDescendants().OfType<Control>().Where(c => c.Name is { Length: > 0 } && c.Name.Contains("Pane")))
        report.AppendLine($"PROBE drawer part {c.Name} {c.GetType().Name} {c.Bounds}");
    report.AppendLine($"PROBE drawer OpenPaneLength {drawer.OpenPaneLength}");
    var dtabs = drawer.GetVisualDescendants().OfType<TabControl>().First();
    for (int i = 0; i < dtabs.ItemCount; i++)
    {
        dtabs.SelectedIndex = i;
        Shot(main, $"touch-drawer-{i}-{(dtabs.Items[i] as TabItem)?.Header}");
    }
    drawer.IsPaneOpen = false;
    for (int t = 0; t < 120; t++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}
// natural minimum: as declared
report.AppendLine($"MainWindow declared MinWidth/MinHeight: 900/560 (window manager will refuse 535)");
Make(main, W, H);
// CW mode and slice B variants
SetProp(vm, "SliceBEnabled", true); Notify(vm, "HasSliceB");
Shot(main, mainPrefix + "main-1024x535-sliceB");
SetProp(vm, "SliceBEnabled", false); Notify(vm, "HasSliceB");
SetProp(vm, "Mode", Zeus.Contracts.RxMode.CWU); Notify(vm, "ModeIsCw"); Notify(vm, "ModeLabel");
Shot(main, mainPrefix + "main-1024x535-cw");
SetProp(vm, "Mode", Zeus.Contracts.RxMode.USB); Notify(vm, "ModeIsCw"); Notify(vm, "ModeLabel");

// ---- Flyouts: render each flyout's content in its own window ----
int fi = 0;
foreach (var c in main.GetLogicalDescendants().OfType<Control>().ToList())
{
    foreach (var fb in new[] { (c as Button)?.Flyout, c.ContextFlyout })
    {
        if (fb is Flyout { Content: Control content } fl)
        {
            fi++;
            var host = new Window { DataContext = c.DataContext, SizeToContent = SizeToContent.WidthAndHeight };
            // Popups opened from the touch layout get its styles, as in the app.
            if (mainPrefix.Length > 0)
                host.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://SimpleHPSDR/"))
                    { Source = new Uri("avares://SimpleHPSDR/Views/TouchStyles.axaml") });
            fl.Content = null;
            host.Content = new Border { Padding = new Thickness(8), Child = content };
            host.Show();
            Pump();
            var nm = $"{mainPrefix}flyout-{fi:00}-{new string(Label(c).Where(char.IsLetterOrDigit).ToArray())}";
            Shot(host, nm);
            host.Content = null; fl.Content = content;
            host.Close();
        }
    }
}

// ---- CW, mini, guide, settings ----
var cw = Make(new CwWindow { DataContext = vm }, 520, 460); cw.Show(); Shot(cw, "cw-520x460-default");
var mini = Make(new MiniWindow { DataContext = vm }, 420, 240); mini.Show(); Shot(mini, "mini-420x240");
try
{
    var guide = new StationGuideViewModel(SimpleHPSDR.Settings.Load(), _ => { });
    var gw = Make(new StationGuideWindow { DataContext = guide }, 800, 600); gw.Show(); Shot(gw, "guide-800x600-default");
    Make(gw, W, H); Shot(gw, "guide-1024x535");
}
catch (Exception e) { Console.WriteLine("guide: " + e.Message); }

var svm = new SettingsViewModel(() => null!) { AppSettings = vm.AppSettings };
var sw = Make(new SettingsWindow { DataContext = svm }, 1040, 720);
sw.Show();
Shot(sw, "settings-1040x720-default");
Make(sw, W, H);
var tabs = sw.GetVisualDescendants().OfType<TabControl>().First();
for (int i = 0; i < tabs.ItemCount; i++)
{
    tabs.SelectedIndex = i;
    var hdr = (tabs.Items[i] as TabItem)?.Header?.ToString() ?? i.ToString();
    Shot(sw, $"settings-1024x535-tab{i:00}-{new string(hdr.Where(char.IsLetterOrDigit).ToArray())}");
}

// ---- Does Holding raise ContextRequested (long-press opens context menus)? ----
var probe = new Button { Content = "probe" };
bool ctx = false; probe.ContextRequested += (_, _) => ctx = true;
var pw = new Window { Content = probe, Width = 200, Height = 100 }; pw.Show(); Pump();
var hctor = typeof(HoldingRoutedEventArgs).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).OrderByDescending(k => k.GetParameters().Length).First();
report.AppendLine("PROBE Holding ctor: " + string.Join(", ", hctor.GetParameters().Select(q => q.ParameterType.Name)));
var hargs = hctor.GetParameters().Select(q => q.ParameterType == typeof(HoldingState) ? (object)HoldingState.Started : q.ParameterType == typeof(Point) ? new Point(10, 10) : q.ParameterType == typeof(PointerType) ? PointerType.Touch : null).ToArray();
var hev = (HoldingRoutedEventArgs)hctor.Invoke(hargs);
try { probe.RaiseEvent(hev); } catch (Exception ex) { report.AppendLine("PROBE raise failed: " + ex.Message); }
report.AppendLine($"PROBE Holding(Started, Touch) -> ContextRequested raised: {ctx}");
ctx = false;
var r2 = typeof(InputElement).Assembly.GetType("Avalonia.Input.HoldingRoutedEventArgs");
report.AppendLine($"PROBE IsHoldingEnabled default: {InputElement.GetIsHoldingEnabled(probe)}, IsHoldWithMouseEnabled default: {InputElement.GetIsHoldWithMouseEnabled(probe)}");
var pinch = typeof(InputElement).Assembly.GetTypes().FirstOrDefault(t => t.Name == "PinchGestureRecognizer");
report.AppendLine($"PROBE PinchGestureRecognizer: {pinch?.FullName} public={pinch?.IsPublic}");
foreach (var t in typeof(InputElement).Assembly.GetTypes().Where(t => t.IsPublic && t.Name.EndsWith("GestureRecognizer")))
    report.AppendLine($"PROBE recognizer: {t.FullName}");

// ---- Long press = right-click (mouse: buttons only) ----
{
    SimpleHPSDR.Controls.LongPress.Enable();
    void Hold(Window w, Point at, int ms)
    {
        w.MouseDown(at, MouseButton.Left);
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        w.MouseUp(at, MouseButton.Left);
        Pump();
    }
    int clicks = 0;
    var flyout = new Flyout { Content = new TextBlock { Text = "menu" } };
    var hb = new Button { Content = "hold me", Width = 120, Height = 48, ContextFlyout = flyout };
    hb.Click += (_, _) => clicks++;
    var hw = new Window { Content = hb, Width = 300, Height = 200 }; hw.Show(); Pump();
    Hold(hw, new Point(60, 100), 100);
    report.AppendLine($"PROBE menu button, short press: clicks {clicks}, menu {flyout.IsOpen}");
    clicks = 0; Hold(hw, new Point(60, 100), 1200);
    report.AppendLine($"PROBE menu button, long press: clicks {clicks}, menu {flyout.IsOpen}");
    flyout.Hide(); hw.Close();

    int plain = 0;
    var pb = new Button { Content = "MOX-like", Width = 120, Height = 48 };
    pb.Click += (_, _) => plain++;
    var plw = new Window { Content = pb, Width = 300, Height = 200 }; plw.Show(); Pump();
    Hold(plw, new Point(60, 100), 1200);
    report.AppendLine($"PROBE plain button, slow click: clicks {plain}");
    plw.Close();

    var sv = new SimpleHPSDR.Controls.SpectrumView();
    int tunes = 0, menus = 0;
    sv.TuneRequested += _ => tunes++;
    sv.MenuRequested += _ => menus++;
    var sw2 = new Window { Content = sv, Width = 600, Height = 300 }; sw2.Show(); Pump();
    Hold(sw2, new Point(300, 80), 1200);
    report.AppendLine($"PROBE spectrum, mouse held still: tunes {tunes}, slice menus {menus}");

    var tb = new TextBox { Text = "select me", Width = 200 };
    var tw = new Window { Content = tb, Width = 300, Height = 200 }; tw.Show(); Pump();
    Hold(tw, new Point(150, 100), 1200);
    report.AppendLine($"PROBE text box, mouse held: context menu {tb.ContextFlyout?.IsOpen == true || (tb.ContextMenu?.IsOpen ?? false)}");
    tw.Close(); sw2.Close();
}

// ---- Flick to glide on the spectrum ----
{
    var fv = new SimpleHPSDR.Controls.SpectrumView { Fling = true };
    typeof(SimpleHPSDR.Controls.SpectrumView).GetField("_hzPerPixel", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(fv, 1.0);
    typeof(SimpleHPSDR.Controls.SpectrumView).GetField("_wfWidth", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(fv, 800);
    var pans = new List<(DateTime T, long Hz)>(); int ended = 0;
    fv.PanRequested += hz => pans.Add((DateTime.UtcNow, hz));
    fv.PanEnded += () => ended++;
    var fw2 = new Window { Content = fv, Width = 800, Height = 300 }; fw2.Show(); Pump();
    void Drag(double from, double to, int stepMs, int pauseMs)
    {
        pans.Clear(); ended = 0;
        fw2.MouseDown(new Point(from, 100), MouseButton.Left);
        for (int i = 1; i <= 10; i++)
        {
            fw2.MouseMove(new Point(from + (to - from) * i / 10, 100));
            Dispatcher.UIThread.RunJobs(); Thread.Sleep(stepMs);
        }
        Thread.Sleep(pauseMs);
        fw2.MouseUp(new Point(to, 100), MouseButton.Left);
        int atRelease = pans.Count;
        // The headless dispatcher doesn't deliver this timer's ticks, so step
        // the glide by hand at its own 16 ms pace while its timer is on.
        var step = typeof(SimpleHPSDR.Controls.SpectrumView).GetMethod("FlingStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var timerField = typeof(SimpleHPSDR.Controls.SpectrumView).GetField("_flingTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until && timerField.GetValue(fv) is DispatcherTimer { IsEnabled: true })
        {
            Thread.Sleep(16);
            step.Invoke(fv, null);
        }
        report.AppendLine($"PROBE fling {from}->{to} in {10 * stepMs} ms, pause {pauseMs} ms: pans during drag {atRelease}, after release {pans.Count - atRelease}, last Hz {(pans.Count > 0 ? pans[^1].Hz : 0)}, glide ms {(pans.Count > atRelease ? (pans[^1].T - pans[atRelease].T).TotalMilliseconds : 0):0}, PanEnded {ended}");
    }
    Drag(500, 300, 8, 0);     // quick flick
    Drag(500, 300, 40, 0);    // slow drag
    Drag(500, 300, 8, 200);   // quick, then held still before release
    fw2.Close();
}

// ---- A height-limited flyout presenter scrolls (what ScreenFit relies on) ----
var tall = new StackPanel { Children = { new Border { Height = 900, Width = 300 } } };
var fp = new FlyoutPresenter { Content = tall, MaxHeight = 519 };
var fw = new Window { Content = fp, Width = 400, Height = 600 }; fw.Show(); Pump();
report.AppendLine($"PROBE FlyoutPresenter MaxHeight 519 with 900 content: height {fp.Bounds.Height:0}, ScrollViewer in template: {fp.GetVisualDescendants().OfType<ScrollViewer>().Any()}");
var mfp = new MenuFlyoutPresenter { MaxHeight = 300 };
for (int i = 0; i < 30; i++) mfp.Items.Add(new MenuItem { Header = $"Item {i}" });
var mw = new Window { Content = mfp, Width = 300, Height = 600 }; mw.Show(); Pump();
report.AppendLine($"PROBE MenuFlyoutPresenter MaxHeight 300 with 30 items: height {mfp.Bounds.Height:0}, ScrollViewer: {mfp.GetVisualDescendants().OfType<ScrollViewer>().Any()}");
// ---- Layout decision ----
report.AppendLine($"PROBE layout status: {svm.LayoutStatus}");
svm.LayoutIndex = 2;
report.AppendLine($"PROBE after choosing Touch: current {SimpleHPSDR.Platform.LayoutMode.Current}; {svm.LayoutStatus}");
svm.LayoutIndex = 0;
report.AppendLine($"PROBE back to Automatic: current {SimpleHPSDR.Platform.LayoutMode.Current}; {svm.LayoutStatus}");
foreach (var w in new[] { 800.0, 1024, 1079, 1080, 1512, 1920 })
    report.AppendLine($"PROBE layout Automatic at {w}: {SimpleHPSDR.Platform.LayoutMode.Resolve("Automatic", w)}");

File.WriteAllText(Path.Combine(outDir, "audit.md"), report.ToString());
Console.WriteLine("done");
return 0;
