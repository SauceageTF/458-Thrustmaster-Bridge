using System.Runtime.InteropServices;

namespace WheelBridge;

/// <summary>
/// Thin P/Invoke over XInput, just enough to find physical Xbox controllers and
/// drive their rumble motors. Used to bounce the rumble a game sends to the
/// virtual wheel onto a real controller strapped to the wheel hub.
/// </summary>
public static class XInputRumble
{
    public const int MaxSlots = 4;
    private const uint ErrorSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;   // large / low-frequency motor
        public ushort RightMotorSpeed;  // small / high-frequency motor
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputSetState(uint userIndex, ref XInputVibration vibration);

    public static bool IsConnected(int slot) =>
        slot is >= 0 and < MaxSlots && XInputGetState((uint)slot, out _) == ErrorSuccess;

    public static IEnumerable<int> ConnectedSlots()
    {
        for (var slot = 0; slot < MaxSlots; slot++)
            if (IsConnected(slot))
                yield return slot;
    }

    /// <summary>Returns false if the slot is empty (controller unplugged).</summary>
    public static bool SetRumble(int slot, byte large, byte small)
    {
        // Xbox 360 feedback is 0-255 per motor; XInput wants 0-65535.
        var vibration = new XInputVibration
        {
            LeftMotorSpeed = (ushort)(large * 257),
            RightMotorSpeed = (ushort)(small * 257),
        };
        return XInputSetState((uint)slot, ref vibration) == ErrorSuccess;
    }
}
