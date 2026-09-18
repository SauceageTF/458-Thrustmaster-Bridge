using System.IO;
using System.IO.Ports;
using Microsoft.Win32;

namespace WheelBridge;

/// <summary>Where the game's rumble gets sent, since the wheel itself has no motor.</summary>
public enum HapticTarget
{
    Off,
    /// <summary>A real Xbox controller plugged into the PC, strapped to the wheel. No extra hardware.</summary>
    XboxController,
    /// <summary>A Raspberry Pi Pico on USB serial driving motors directly. See pico/main.py.</summary>
    PicoSerial,
}

/// <summary>
/// Snapshot of the haptic link, safe to read from any thread.
/// </summary>
public sealed record HapticState(bool Connected, string? TargetName, byte LargeMotor, byte SmallMotor)
{
    public static readonly HapticState Initial = new(false, null, 0, 0);
}

/// <summary>
/// Forwards Xbox 360 rumble (which games already send to the ViGEm virtual pad)
/// to something that can actually vibrate: a physical Xbox controller via
/// XInput, or a Raspberry Pi Pico over USB serial.
///
/// Pico wire protocol (PC -> Pico, 115200 8N1): 3-byte frames, <c>0xAA, large, small</c>,
/// each motor 0-255. The sync byte lets the Pico resync mid-stream. Frames go out
/// on every change and as a keepalive every <see cref="KeepaliveMs"/> so the Pico
/// can cut the motors if the PC side dies mid-rumble. See pico/main.py.
/// </summary>
public sealed class HapticOutput : IDisposable
{
    public const string AutoPort = "auto";
    private const int BaudRate = 115200;
    private const byte SyncByte = 0xAA;
    private const int KeepaliveMs = 200;
    private const int ReconnectMs = 2000;
    private const int SlotRescanMs = 1000;

    // Raspberry Pi's USB vendor ID; the Pico shows up as VID_2E8A&PID_0005 under
    // MicroPython and VID_2E8A&PID_000A under the C SDK's stdio_usb.
    private const string PicoVidPrefix = "VID_2E8A";

    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private HapticState _state = HapticState.Initial;
    private byte _large, _small;
    private DateTime _testUntil = DateTime.MinValue;
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    /// <summary>Same instance the bridge service reads; swapped wholesale by the UI.</summary>
    public volatile WheelSettings Settings = new();

    /// <summary>No virtual pad exists right now, so every XInput slot is a physical controller.</summary>
    public const int NoVirtualPad = -1;

    /// <summary>A virtual pad exists but XInput hasn't told us its slot yet; rumbling blindly could echo through FeedbackReceived.</summary>
    public const int VirtualPadSlotUnknown = -2;

    /// <summary>
    /// XInput slot the virtual wheel occupies, so we never rumble ourselves, or one
    /// of <see cref="NoVirtualPad"/> / <see cref="VirtualPadSlotUnknown"/>.
    /// </summary>
    public volatile int VirtualPadSlot = NoVirtualPad;

    public HapticState CurrentState
    {
        get { lock (_lock) return _state; }
    }

    public void Start()
    {
        if (_thread is not null)
            return;

        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "WheelBridge-Haptics" };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Called from ViGEm's feedback callback; must not block, so it just stashes and wakes the writer.</summary>
    public void SetRumble(byte large, byte small)
    {
        lock (_lock)
        {
            _large = large;
            _small = small;
        }
        _wake.Set();
    }

    /// <summary>Drives both motors at full for a moment so the user can confirm the setup without launching a game.</summary>
    public void Pulse(TimeSpan duration)
    {
        lock (_lock) _testUntil = DateTime.UtcNow + duration;
        _wake.Set();
    }

    private void SetState(HapticState state)
    {
        lock (_lock) _state = state;
    }

    /// <summary>Current motor levels after intensity scaling (or full-on during a test pulse).</summary>
    private (byte large, byte small) CurrentLevels(WheelSettings settings)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow < _testUntil)
                return (255, 255);
            return (Scale(_large, settings.HapticIntensity), Scale(_small, settings.HapticIntensity));
        }
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var target = Settings.HapticTarget;
            switch (target)
            {
                case HapticTarget.XboxController:
                    RunXInput(token);
                    break;
                case HapticTarget.PicoSerial:
                    RunPico(token);
                    break;
                default:
                    SetState(HapticState.Initial);
                    _wake.WaitOne(ReconnectMs);
                    break;
            }
        }

        SetState(HapticState.Initial);
    }

    /// <summary>
    /// Rumbles every physical controller except the virtual wheel. Returns when
    /// the target setting changes so <see cref="Run"/> can switch outputs.
    /// </summary>
    private void RunXInput(CancellationToken token)
    {
        var slots = new List<int>();
        var lastScan = DateTime.MinValue;
        byte lastLarge = 255, lastSmall = 255;
        var lastSent = DateTime.MinValue;

        while (!token.IsCancellationRequested)
        {
            _wake.WaitOne(KeepaliveMs);

            var settings = Settings;
            if (settings.HapticTarget != HapticTarget.XboxController)
                break;

            // Controllers come and go (and the virtual pad's slot is only known once
            // ViGEm has connected), so keep the slot list fresh but not per-frame.
            if ((DateTime.UtcNow - lastScan).TotalMilliseconds > SlotRescanMs)
            {
                var ownSlot = VirtualPadSlot;
                slots = XInputRumble.ConnectedSlots().Where(s => s != ownSlot).ToList();
                lastScan = DateTime.UtcNow;
                if (slots.Count == 0 || ownSlot == VirtualPadSlotUnknown)
                {
                    // A virtual pad we can't identify might be in the list -- rumbling it would echo.
                    SetState(new HapticState(false, null, 0, 0));
                    lastLarge = lastSmall = 255;
                    continue;
                }
            }
            if (slots.Count == 0)
                continue;

            var (large, small) = CurrentLevels(settings);
            var changed = large != lastLarge || small != lastSmall;
            if (!changed && (DateTime.UtcNow - lastSent).TotalMilliseconds < KeepaliveMs)
                continue;

            foreach (var slot in slots)
                XInputRumble.SetRumble(slot, large, small);
            lastLarge = large;
            lastSmall = small;
            lastSent = DateTime.UtcNow;

            var name = string.Join("+", slots.Select(s => $"#{s + 1}"));
            SetState(new HapticState(true, $"XBOX {name}", large, small));
        }

        foreach (var slot in slots)
            XInputRumble.SetRumble(slot, 0, 0);
        SetState(HapticState.Initial);
    }

    private void RunPico(CancellationToken token)
    {
        var configured = Settings.HapticPort?.Trim() ?? "";
        var portName = configured.Equals(AutoPort, StringComparison.OrdinalIgnoreCase) ? FindPicoPort() : configured;
        if (portName is null || portName.Length == 0)
        {
            SetState(HapticState.Initial);
            _wake.WaitOne(ReconnectMs);
            return;
        }

        SerialPort? port = null;
        try
        {
            port = new SerialPort(portName, BaudRate)
            {
                // MicroPython's USB CDC discards writes until the host raises DTR.
                DtrEnable = true,
                WriteTimeout = 500,
            };
            port.Open();
            SetState(new HapticState(true, portName, 0, 0));

            var frame = new byte[3];
            byte lastLarge = 255, lastSmall = 255; // force an initial (zero) frame
            var lastSent = DateTime.MinValue;

            while (!token.IsCancellationRequested)
            {
                _wake.WaitOne(KeepaliveMs);

                // Re-read settings each frame: the target/port may have been changed in the UI.
                var settings = Settings;
                if (settings.HapticTarget != HapticTarget.PicoSerial ||
                    !string.Equals(settings.HapticPort?.Trim(), configured, StringComparison.OrdinalIgnoreCase))
                    break;

                var (large, small) = CurrentLevels(settings);
                var changed = large != lastLarge || small != lastSmall;
                if (!changed && (DateTime.UtcNow - lastSent).TotalMilliseconds < KeepaliveMs)
                    continue;

                frame[0] = SyncByte;
                frame[1] = large;
                frame[2] = small;
                port.Write(frame, 0, frame.Length);
                lastLarge = large;
                lastSmall = small;
                lastSent = DateTime.UtcNow;
                SetState(new HapticState(true, portName, large, small));
            }

            // Leave the motors off when we let go of the port on purpose.
            if (port.IsOpen)
                port.Write(new[] { SyncByte, (byte)0, (byte)0 }, 0, 3);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            // Pico unplugged, port grabbed by Thonny, etc. -- fall through and retry.
            SetState(HapticState.Initial);
            _wake.WaitOne(ReconnectMs);
        }
        finally
        {
            try { port?.Dispose(); } catch { /* port already gone */ }
            SetState(HapticState.Initial);
        }
    }

    private static byte Scale(byte value, double intensity) =>
        (byte)Math.Clamp(Math.Round(value * Math.Clamp(intensity, 0.0, 2.0)), 0, 255);

    /// <summary>
    /// Finds the COM port of a plugged-in Pico by matching Raspberry Pi's USB VID
    /// in the registry's device enumeration against the ports that currently exist.
    /// </summary>
    public static string? FindPicoPort()
    {
        var present = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        if (present.Count == 0)
            return null;

        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb is null)
                return null;

            foreach (var deviceKeyName in usb.GetSubKeyNames())
            {
                if (!deviceKeyName.StartsWith(PicoVidPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                using var deviceKey = usb.OpenSubKey(deviceKeyName);
                if (deviceKey is null)
                    continue;

                foreach (var instanceName in deviceKey.GetSubKeyNames())
                {
                    using var parameters = deviceKey.OpenSubKey(Path.Combine(instanceName, "Device Parameters"));
                    if (parameters?.GetValue("PortName") is string portName && present.Contains(portName))
                        return portName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Registry unreadable; the user can still type a COM port manually.
        }

        return null;
    }
}
