using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace MusicOrganiser;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Held for the whole process so the mutex isn't GC-collected (which would release it early).
    private Mutex? _instanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: if another instance is already running AND we can bring its window to
        // the front, this duplicate bows out. If no focusable window is found (a stuck or exiting
        // instance still holding the mutex), start normally instead of vanishing with no feedback.
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\MusicOrganiser.SingleInstance", out var createdNew);
        if (!createdNew && TryActivateExistingInstance())
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    // Restore (un-minimize) and bring the already-running instance's window to the front.
    // Returns false if there's no other instance with a window to activate.
    private static bool TryActivateExistingInstance()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            var other = Process.GetProcessesByName(me.ProcessName)
                .FirstOrDefault(p => p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero);
            if (other == null) return false;
            ShowWindow(other.MainWindowHandle, SW_RESTORE);
            SetForegroundWindow(other.MainWindowHandle);
            return true;
        }
        catch { return false; }
    }
}
