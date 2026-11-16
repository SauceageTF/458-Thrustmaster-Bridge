using System.Text.Json.Serialization;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace WheelBridge;

/// <summary>The wheel's physical buttons, named by what's printed on them. Names are what settings.json stores.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WheelButton>))]
public enum WheelButton { A, B, X, Y, LeftPaddle, RightPaddle, Start, Back, Up, Down, Left, Right }

/// <summary>What a wheel button can send to the game: any Xbox 360 button, or nothing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PadButton>))]
public enum PadButton { None, A, B, X, Y, LB, RB, Back, Start, Guide, DPadUp, DPadDown, DPadLeft, DPadRight, LeftStick, RightStick }

/// <summary>Where each wheel button lives in an Input frame, and what each pad button means to ViGEm.</summary>
public static class ButtonMapping
{
    public static readonly int WheelButtonCount = Enum.GetValues<WheelButton>().Length;
    public static readonly int PadButtonCount = Enum.GetValues<PadButton>().Length;

    /// <summary>Byte offset (including the 4-byte GIP header) and bit of each button. See the README's protocol table.</summary>
    public static readonly (WheelButton Button, int Offset, byte Mask)[] InputBits =
    {
        (WheelButton.Start, 4, 0x04),
        (WheelButton.Back, 4, 0x08),
        (WheelButton.A, 4, 0x10),
        (WheelButton.B, 4, 0x20),
        (WheelButton.X, 4, 0x40),
        (WheelButton.Y, 4, 0x80),
        (WheelButton.Up, 5, 0x01),
        (WheelButton.Down, 5, 0x02),
        (WheelButton.Left, 5, 0x04),
        (WheelButton.Right, 5, 0x08),
        (WheelButton.LeftPaddle, 5, 0x10),
        (WheelButton.RightPaddle, 5, 0x20),
    };

    /// <summary>Every pad button except <see cref="PadButton.None"/>, so the bridge can set (and clear) all of them each frame.</summary>
    public static readonly (PadButton Pad, Xbox360Button Xbox)[] XboxButtons =
    {
        (PadButton.A, Xbox360Button.A),
        (PadButton.B, Xbox360Button.B),
        (PadButton.X, Xbox360Button.X),
        (PadButton.Y, Xbox360Button.Y),
        (PadButton.LB, Xbox360Button.LeftShoulder),
        (PadButton.RB, Xbox360Button.RightShoulder),
        (PadButton.Back, Xbox360Button.Back),
        (PadButton.Start, Xbox360Button.Start),
        (PadButton.Guide, Xbox360Button.Guide),
        (PadButton.DPadUp, Xbox360Button.Up),
        (PadButton.DPadDown, Xbox360Button.Down),
        (PadButton.DPadLeft, Xbox360Button.Left),
        (PadButton.DPadRight, Xbox360Button.Right),
        (PadButton.LeftStick, Xbox360Button.LeftThumb),
        (PadButton.RightStick, Xbox360Button.RightThumb),
    };

    /// <summary>Out of the box each button sends its Xbox namesake; the paddles are the shoulder buttons.</summary>
    public static PadButton DefaultFor(WheelButton button) => button switch
    {
        WheelButton.A => PadButton.A,
        WheelButton.B => PadButton.B,
        WheelButton.X => PadButton.X,
        WheelButton.Y => PadButton.Y,
        WheelButton.LeftPaddle => PadButton.LB,
        WheelButton.RightPaddle => PadButton.RB,
        WheelButton.Start => PadButton.Start,
        WheelButton.Back => PadButton.Back,
        WheelButton.Up => PadButton.DPadUp,
        WheelButton.Down => PadButton.DPadDown,
        WheelButton.Left => PadButton.DPadLeft,
        WheelButton.Right => PadButton.DPadRight,
        _ => PadButton.None,
    };

    public static string Label(WheelButton button) => button switch
    {
        WheelButton.LeftPaddle => "Left paddle",
        WheelButton.RightPaddle => "Right paddle",
        WheelButton.Up => "D-pad up",
        WheelButton.Down => "D-pad down",
        WheelButton.Left => "D-pad left",
        WheelButton.Right => "D-pad right",
        _ => button.ToString(),
    };

    public static string Label(PadButton button) => button switch
    {
        PadButton.None => "Nothing",
        PadButton.LB => "LB",
        PadButton.RB => "RB",
        PadButton.Guide => "Guide (Xbox)",
        PadButton.DPadUp => "D-pad up",
        PadButton.DPadDown => "D-pad down",
        PadButton.DPadLeft => "D-pad left",
        PadButton.DPadRight => "D-pad right",
        PadButton.LeftStick => "L-stick click",
        PadButton.RightStick => "R-stick click",
        _ => button.ToString(),
    };

    /// <summary>Reads which physical buttons are down from an Input frame into <paramref name="down"/> (indexed by <see cref="WheelButton"/>).</summary>
    public static void ReadPressed(byte[] frame, bool[] down)
    {
        foreach (var (button, offset, mask) in InputBits)
            down[(int)button] = (frame[offset] & mask) != 0;
    }
}

/// <summary>
/// Turns the physical buttons held right now into the pad buttons the game should
/// see, applying a profile's remapping and shift layer. Keeps a little state, so
/// use one instance per bridge connection, from one thread.
///
/// Each button picks its layer at the moment it's pressed and keeps it until it's
/// released. Otherwise letting go of shift while still holding a shifted button
/// would release one pad button and press another mid-hold.
/// </summary>
public sealed class ButtonLayerResolver
{
    private readonly bool[] _wasDown = new bool[ButtonMapping.WheelButtonCount];
    private readonly bool[] _pressedWithShift = new bool[ButtonMapping.WheelButtonCount];

    /// <param name="down">Physical buttons held, indexed by <see cref="WheelButton"/>.</param>
    /// <param name="padOut">Filled with the pad buttons to press, indexed by <see cref="PadButton"/>.</param>
    /// <returns>Whether the shift button is held.</returns>
    public bool Resolve(bool[] down, TuningProfile profile, bool[] padOut)
    {
        Array.Clear(padOut);
        var shiftButton = profile.ShiftButton;
        var shiftHeld = shiftButton is { } s && down[(int)s];

        for (var i = 0; i < down.Length; i++)
        {
            if (!down[i])
            {
                _wasDown[i] = false;
                continue;
            }

            var button = (WheelButton)i;
            if (!_wasDown[i])
            {
                _wasDown[i] = true;
                _pressedWithShift[i] = shiftHeld;
            }

            // The shift button is a modifier only; it sends nothing itself.
            if (button == shiftButton)
                continue;

            var target = profile.Resolve(button, _pressedWithShift[i]);
            if (target != PadButton.None)
                padOut[(int)target] = true;
        }

        return shiftHeld;
    }
}
