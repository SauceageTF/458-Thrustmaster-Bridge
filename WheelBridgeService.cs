using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace WheelBridge;

/// <summary>
/// Snapshot of the wheel's current state, safe to read from any thread.
/// </summary>
public sealed record WheelState(
    bool WheelConnected,
    bool VigemConnected,
    double Wheel,
    double Throttle,
    double Brake,
    bool A, bool B, bool X, bool Y,
    bool LeftShoulder, bool RightShoulder,
    bool Start, bool Back,
    bool Up, bool Down, bool Left, bool Right,
    string? StatusMessage)
{
    public static readonly WheelState Initial = new(
        false, false, 0, 0, 0,
        false, false, false, false,
        false, false,
        false, false,
        false, false, false, false,
        "Starting...");
}

/// <summary>
/// Reads raw USB reports from the Thrustmaster Ferrari 458 Spider (VID 0x044F,
/// PID 0xB671) -- bound to WinUSB via Zadig, since Windows' own Xbox accessory
/// driver claims the device but never finishes initializing it for
/// Windows.Gaming.Input/XInput/GIP -- and republishes them as a virtual Xbox 360
/// controller via ViGEmBus. See README.md for the reverse-engineered report format.
/// </summary>
public sealed class WheelBridgeService : IDisposable
{
    private const int VendorId = 0x044F;
    private const int ProductId = 0xB671;

    // GIP (Gaming Input Protocol) frame command IDs. This device is a real GIP
    // accessory (declares the MS_COMP_XGIP10 compatible ID) -- it doesn't just
    // free-stream input reports, it sends an Announce and then waits for the
    // host to complete a small handshake before it starts sending Input frames.
    private const byte GipCmdAnnounce = 0x02;
    private const byte GipCmdPowerMode = 0x05;
    private const byte GipCmdLedMode = 0x0A;
    private const byte GipCmdSerialNumber = 0x1E;
    private const byte GipCmdInput = 0x20;
    private const byte GipTypeRequest = 0x02;
    private const byte GipTypeRequestAck = 0x03;

    // Offsets into a Cmd_Input frame (4-byte GIP header + payload).
    private const int WheelOffset = 6;
    private const int ThrottleOffset = 8;
    private const int BrakeOffset = 10;
    private const int Button1Offset = 4;
    private const int Button2Offset = 5;

    private static readonly (byte mask, Xbox360Button button)[] Button1Map =
    {
        (0x04, Xbox360Button.Start),
        (0x08, Xbox360Button.Back),
        (0x10, Xbox360Button.A),
        (0x20, Xbox360Button.B),
        (0x40, Xbox360Button.X),
        (0x80, Xbox360Button.Y),
    };

    private static readonly (byte mask, Xbox360Button button)[] Button2Map =
    {
        (0x01, Xbox360Button.Up),
        (0x02, Xbox360Button.Down),
        (0x04, Xbox360Button.Left),
        (0x08, Xbox360Button.Right),
        (0x10, Xbox360Button.LeftShoulder),
        (0x20, Xbox360Button.RightShoulder),
    };

    private readonly object _lock = new();
    private WheelState _state = WheelState.Initial;
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    private volatile WheelSettings _settings = new();

    /// <summary>Read on every packet from the bridge thread; assign a whole new instance to update (no partial-write races).</summary>
    public WheelSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            Haptics.Settings = value;
        }
    }

    /// <summary>Rumble the game sends to the virtual pad, forwarded to an external motor since the wheel has none.</summary>
    public HapticOutput Haptics { get; } = new();

    public event Action<WheelState>? StateChanged;

    public WheelState CurrentState
    {
        get { lock (_lock) return _state; }
    }

    public void Start()
    {
        if (_thread is not null)
            return;

        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "WheelBridge" };
        _thread.Start();
        Haptics.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        Haptics.Stop();
    }

    public void Dispose() => Stop();

    private void SetState(WheelState state)
    {
        lock (_lock) _state = state;
        StateChanged?.Invoke(state);
    }

    private static double ApplyAxisCurve(double value01, double deadzone, double curve)
    {
        value01 = Math.Clamp(value01, 0.0, 1.0);
        if (value01 <= deadzone)
            return 0.0;

        var rescaled = (value01 - deadzone) / (1.0 - deadzone);
        return Math.Clamp(Math.Pow(rescaled, curve), 0.0, 1.0);
    }

    /// <summary>
    /// Completes the GIP handshake (power on, LED, serial number request) that this
    /// device requires before it will start sending Input frames. Modeled on the
    /// verified-working handshake in medusalix/xow (controller/controller.cpp).
    /// </summary>
    private static void SendGipHandshake(UsbEndpointWriter writer, byte deviceId)
    {
        byte seq = 1;
        byte TypeByte(byte type) => (byte)(((type & 0x0F) << 4) | (deviceId & 0x0F));

        writer.Write(new byte[] { GipCmdPowerMode, TypeByte(GipTypeRequest), seq++, 0x01, 0x00 }, 500, out _);
        writer.Write(new byte[] { GipCmdLedMode, TypeByte(GipTypeRequest), seq++, 0x03, 0x00, 0x01, 0x14 }, 500, out _);
        writer.Write(new byte[] { GipCmdSerialNumber, TypeByte(GipTypeRequestAck), seq++, 0x01, 0x04 }, 500, out _);
    }

    /// <summary>
    /// Works out which XInput slot the virtual pad landed in. ViGEmBus can report it
    /// directly on recent drivers; otherwise fall back to "whichever slot appeared
    /// after Connect", giving XInput a moment to notice the new device.
    /// </summary>
    private static int ResolveVirtualPadSlot(IXbox360Controller pad, HashSet<int> slotsBefore)
    {
        try
        {
            return pad.UserIndex;
        }
        catch (Exception)
        {
            // Older ViGEmBus, or the index isn't assigned yet -- diff instead.
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var added = XInputRumble.ConnectedSlots().Where(s => !slotsBefore.Contains(s)).ToList();
            if (added.Count == 1)
                return added[0];
            Thread.Sleep(100);
        }
        return HapticOutput.VirtualPadSlotUnknown;
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UsbContext? context = null;
            IDisposable? deviceCollection = null;
            IUsbDevice? device = null;
            ViGEmClient? client = null;

            try
            {
                context = new UsbContext();
                // Keep the whole enumerated collection alive (and dispose it, below) for as
                // long as we hold any device from it -- libusb refcounts every enumerated
                // device (not just the one we pick), and disposing the context while other
                // entries are still only GC-finalized (not explicitly released) corrupts
                // native state (AccessViolationException from LibUsbDotNet.NativeMethods.UnrefDevice).
                var collection = context.List();
                deviceCollection = collection;
                var found = collection.FirstOrDefault(d => d.VendorId == VendorId && d.ProductId == ProductId);
                if (found is null)
                {
                    SetState(CurrentState with { WheelConnected = false, StatusMessage = "Waiting for wheel..." });
                    Thread.Sleep(1000);
                    continue;
                }

                device = found;
                if (!device.TryOpen())
                {
                    SetState(CurrentState with { WheelConnected = false, StatusMessage = "Found wheel but failed to open it, retrying..." });
                    Thread.Sleep(2000);
                    continue;
                }

                device.ClaimInterface(device.Configs[0].Interfaces[0].Number);
                var reader = device.OpenEndpointReader(ReadEndpointID.Ep01, 64, EndpointType.Interrupt);
                var writer = device.OpenEndpointWriter(WriteEndpointID.Ep01, EndpointType.Interrupt);

                // The device announces itself (GIP Announce frame) as soon as the interface
                // is claimed, then sits idle until the handshake below is sent -- it will
                // never send real Input frames on its own.
                var announceBuf = new byte[64];
                if (reader.Read(announceBuf, 1000, out int announceBytes) == Error.Success &&
                    announceBytes >= 4 && announceBuf[0] == GipCmdAnnounce)
                {
                    byte deviceId = (byte)(announceBuf[1] & 0x0F);
                    SendGipHandshake(writer, deviceId);
                }

                try
                {
                    client = new ViGEmClient();
                }
                catch (VigemBusNotFoundException)
                {
                    SetState(CurrentState with { VigemConnected = false, StatusMessage = "ViGEmBus not found, retrying... (is it installed?)" });
                    Thread.Sleep(2000);
                    continue;
                }

                var pad = client.CreateXbox360Controller();
                // Games rumble the virtual pad like any Xbox 360 controller; the
                // wheel can't act on it, so hand it to the external motor driver.
                pad.FeedbackReceived += (_, fb) => Haptics.SetRumble(fb.LargeMotor, fb.SmallMotor);
                var slotsBefore = XInputRumble.ConnectedSlots().ToHashSet();
                pad.Connect();
                Haptics.VirtualPadSlot = ResolveVirtualPadSlot(pad, slotsBefore);
                SetState(CurrentState with { WheelConnected = true, VigemConnected = true, StatusMessage = "Connected" });

                var buf = new byte[64];
                while (!token.IsCancellationRequested)
                {
                    var err = reader.Read(buf, 500, out int bytesRead);
                    if (err == Error.NoDevice)
                    {
                        SetState(CurrentState with { WheelConnected = false, StatusMessage = "Wheel disconnected, waiting for reconnect..." });
                        break;
                    }
                    // Timeouts, short reads, and non-Input GIP frames (e.g. a repeated
                    // Announce) are all just skipped -- only Input frames carry live state.
                    if (err != Error.Success || bytesRead < 12 || buf[0] != GipCmdInput)
                        continue;

                    var settings = Settings;

                    var wheelRaw = BitConverter.ToUInt16(buf, WheelOffset);
                    var throttleRaw = BitConverter.ToUInt16(buf, ThrottleOffset);
                    var brakeRaw = BitConverter.ToUInt16(buf, BrakeOffset);

                    var wheel = (wheelRaw - 32768) / 32768.0 * settings.SteeringSensitivity;
                    if (Math.Abs(wheel) < settings.SteeringDeadzone)
                        wheel = 0;
                    wheel = Math.Clamp(wheel, -1.0, 1.0);
                    if (settings.InvertSteering)
                        wheel = -wheel;

                    var throttle = ApplyAxisCurve(throttleRaw / 1023.0, settings.ThrottleDeadzone, settings.PedalCurve);
                    var brake = ApplyAxisCurve(brakeRaw / 1023.0, settings.BrakeDeadzone, settings.PedalCurve);

                    pad.SetAxisValue(Xbox360Axis.LeftThumbX, (short)Math.Clamp(wheel * short.MaxValue, short.MinValue, short.MaxValue));
                    pad.SetSliderValue(Xbox360Slider.RightTrigger, (byte)(throttle * 255));
                    pad.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)(brake * 255));

                    var b1 = buf[Button1Offset];
                    var b2 = buf[Button2Offset];
                    foreach (var (mask, button) in Button1Map)
                        pad.SetButtonState(button, (b1 & mask) != 0);
                    foreach (var (mask, button) in Button2Map)
                        pad.SetButtonState(button, (b2 & mask) != 0);

                    SetState(new WheelState(
                        true, true, wheel, throttle, brake,
                        A: (b1 & 0x10) != 0, B: (b1 & 0x20) != 0, X: (b1 & 0x40) != 0, Y: (b1 & 0x80) != 0,
                        LeftShoulder: (b2 & 0x10) != 0, RightShoulder: (b2 & 0x20) != 0,
                        Start: (b1 & 0x04) != 0, Back: (b1 & 0x08) != 0,
                        Up: (b2 & 0x01) != 0, Down: (b2 & 0x02) != 0, Left: (b2 & 0x04) != 0, Right: (b2 & 0x08) != 0,
                        StatusMessage: "Connected"));
                }
            }
            catch (Exception ex)
            {
                SetState(CurrentState with { WheelConnected = false, VigemConnected = false, StatusMessage = $"Error: {ex.Message}, retrying..." });
                Thread.Sleep(2000);
            }
            finally
            {
                // The pad is going away, so no more feedback events will arrive to clear a held rumble.
                Haptics.SetRumble(0, 0);
                Haptics.VirtualPadSlot = HapticOutput.NoVirtualPad;
                client?.Dispose();
                device?.Close();
                deviceCollection?.Dispose();
                context?.Dispose();
            }
        }
    }
}
