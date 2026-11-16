using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WheelBridge;

/// <summary>
/// Reports the executable of whichever window comes to the front, so a game's
/// profile can be switched to when it starts or is alt-tabbed back into. Uses a
/// WinEvent hook rather than polling: Windows calls back on the thread that
/// created this (the UI thread, whose message loop delivers the events).
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    // Held in a field so the GC doesn't collect the delegate while Windows still calls it.
    private readonly WinEventProc _callback;
    private IntPtr _hook;

    /// <summary>Lower-case executable file name of the new foreground window, e.g. "forzahorizon5.exe".</summary>
    public event Action<string>? ForegroundChanged;

    public ForegroundWatcher()
    {
        _callback = OnForegroundEvent;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    /// <summary>The foreground window's executable right now, or null if it can't be told.</summary>
    public static string? CurrentExe() => ExeForWindow(GetForegroundWindow());

    private void OnForegroundEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ExeForWindow(hwnd) is { } exe)
            ForegroundChanged?.Invoke(exe);
    }

    private static string? ExeForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return null;

        // Limited-information access works on most processes, including elevated ones.
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                if (QueryFullProcessImageName(process, 0, buffer, ref size))
                    return TuningProfile.NormalizeGame(Path.GetFileName(buffer.ToString()));
            }
            finally
            {
                CloseHandle(process);
            }
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return TuningProfile.NormalizeGame(p.ProcessName);
        }
        catch (Exception)
        {
            return null; // exited already, or protected
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc callback, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
