using System.IO.Pipes;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SimpleHPSDR.Platform;

/// <summary>
/// One SimpleHPSDR per user. A second start asks the running copy to come to the
/// front and quits. Two copies used to fight: the second stopped the first one's
/// engine, couldn't open engine.log because the first held it, and sat at
/// "No radio" with no radio picker.
///
/// A copy that is hung (it doesn't bring its window forward within a few seconds)
/// doesn't block a new start: the new copy carries on and takes over the radio.
/// </summary>
public static class SingleInstance
{
    private const byte Show = 1, Shown = 2;
    private static Mutex? _mutex;

    private static string Name
    {
        get
        {
            var user = new string(Environment.UserName.Where(char.IsAsciiLetterOrDigit).ToArray());
            return $"SimpleHPSDR-{(user.Length > 0 ? user : "user")}";
        }
    }

    /// <summary>False when a running copy came to the front, and this one should quit.</summary>
    public static bool Claim()
    {
        try
        {
            _mutex = new Mutex(true, Name, out bool created);
            if (created) return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return true;   // can't tell; don't stand in the way
        }
        return !AskRunningCopyToShow();
    }

    private static bool AskRunningCopyToShow()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut);
            pipe.Connect(2000);
            pipe.WriteByte(Show);
            pipe.Flush();
            var reply = new byte[1];
            var read = pipe.ReadAsync(reply).AsTask();
            return read.Wait(4000) && read.Result == 1 && reply[0] == Shown;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>Answer later starts by bringing this window forward.</summary>
    public static void Listen(Window window)
    {
        var thread = new Thread(() => Serve(window)) { IsBackground = true, Name = "SingleInstance" };
        thread.Start();
    }

    private static void Serve(Window window)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1);
                pipe.WaitForConnection();
                if (pipe.ReadByte() != Show) continue;
                // Answer only once the window really came forward, so a copy whose
                // UI is stuck doesn't keep a new start out.
                var shown = Dispatcher.UIThread.InvokeAsync(() => BringForward(window));
                if (!shown.GetTask().Wait(3000)) continue;
                pipe.WriteByte(Shown);
                pipe.Flush();
            }
            catch (IOException)
            {
                // Another copy (a hung one) already serves the name: nobody to answer.
                Thread.Sleep(5000);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
        }
    }

    private static void BringForward(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        // Windows won't let another program take the foreground; a moment on top does.
        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
    }
}
