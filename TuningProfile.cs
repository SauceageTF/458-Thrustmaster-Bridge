using System.IO;
using System.Text.Json.Serialization;

namespace WheelBridge;

/// <summary>
/// One named set of feel settings, e.g. per game. Everything here is a matter of
/// taste; things that describe the hardware (calibration, rumble output device)
/// live on <see cref="WheelSettings"/> and are shared by all profiles.
///
/// Property names match the pre-profiles flat settings.json, so an old file can
/// be deserialized straight into a profile (see <see cref="WheelSettings.Load"/>).
/// </summary>
public sealed class TuningProfile
{
    public string Name { get; set; } = WheelSettings.DefaultProfileName(1);

    /// <summary>Multiplies wheel deflection. &gt;1 amplifies a bungee wheel's short rotation range to reach full lock in-game.</summary>
    public double SteeringSensitivity { get; set; } = 1.0;

    /// <summary>Fraction of physical travel near center ignored, to absorb bungee spring slack/jitter at rest.</summary>
    public double SteeringDeadzone { get; set; } = 0.03;

    /// <summary>Steering response curve: 1.0 = linear, &gt;1 = calmer near center for small corrections, &lt;1 = twitchier near center.</summary>
    public double SteeringCurve { get; set; } = 1.0;

    public bool InvertSteering { get; set; }

    /// <summary>Fraction of pedal travel near the resting position ignored.</summary>
    public double ThrottleDeadzone { get; set; } = 0.02;

    public double BrakeDeadzone { get; set; } = 0.02;

    /// <summary>Throttle response curve: 1.0 = linear, &lt;1 = more bite early in the travel, &gt;1 = more gradual initial travel.</summary>
    public double ThrottleCurve { get; set; } = 1.0;

    /// <summary>Brake response curve, same scale as <see cref="ThrottleCurve"/>. &gt;1 makes it harder to lock the brakes.</summary>
    public double BrakeCurve { get; set; } = 1.0;

    /// <summary>Pre-1.2 single curve shared by both pedals. Only read, to seed the per-pedal curves; never written back.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PedalCurve { get; set; }

    /// <summary>Multiplies the game's rumble strength before it reaches the motors. &gt;1 boosts weak motors.</summary>
    public double HapticIntensity { get; set; } = 1.0;

    // Button maps are read by the bridge thread on every packet, so they're
    // replaced wholesale (see WithMapping), never edited in place. Only buttons
    // that differ from the default are stored.

    /// <summary>Wheel button -> what the game receives. A button not listed sends <see cref="ButtonMapping.DefaultFor"/>.</summary>
    public IReadOnlyDictionary<WheelButton, PadButton> ButtonMap { get; set; } = new Dictionary<WheelButton, PadButton>();

    /// <summary>While held, other buttons use <see cref="ShiftMap"/>. Null = no shift layer.</summary>
    public WheelButton? ShiftButton { get; set; }

    /// <summary>What buttons send while <see cref="ShiftButton"/> is held. A button not listed sends the same as without shift.</summary>
    public IReadOnlyDictionary<WheelButton, PadButton> ShiftMap { get; set; } = new Dictionary<WheelButton, PadButton>();

    /// <summary>
    /// Executable file names (lower case, e.g. "forzahorizon5.exe") that switch to
    /// this profile when their window comes to the front. Each game belongs to at
    /// most one profile. Only touched from the UI thread.
    /// </summary>
    public List<string> Games { get; set; } = new();

    // Allowed ranges, matching the SETTINGS sliders. Imported and hand-edited files are clamped to these.
    public const double MinSensitivity = 0.5, MaxSensitivity = 3.0;
    public const double MaxSteeringDeadzone = 0.15, MaxPedalDeadzone = 0.2;
    public const double MinCurve = 0.5, MaxSteeringCurve = 3.0, MaxPedalCurve = 2.0;
    public const double MaxHapticIntensity = 2.0;
    public const int MaxNameLength = 40;

    /// <summary>What <paramref name="button"/> sends to the game, on the shift layer or not.</summary>
    public PadButton Resolve(WheelButton button, bool shifted)
    {
        if (shifted && ShiftMap.TryGetValue(button, out var onShift))
            return onShift;
        return ButtonMap.TryGetValue(button, out var mapped) ? mapped : ButtonMapping.DefaultFor(button);
    }

    /// <summary>
    /// Sets one button's mapping by publishing a new map. <paramref name="target"/>
    /// null means "default" (normal layer) or "same as without shift" (shift layer).
    /// </summary>
    public void SetMapping(WheelButton button, bool shiftLayer, PadButton? target)
    {
        var map = new Dictionary<WheelButton, PadButton>(shiftLayer ? ShiftMap : ButtonMap);
        if (target is null || (!shiftLayer && target == ButtonMapping.DefaultFor(button)))
            map.Remove(button);
        else
            map[button] = target.Value;

        if (shiftLayer)
            ShiftMap = map;
        else
            ButtonMap = map;
    }

    /// <summary>Back to each button sending its namesake, with no shift layer.</summary>
    public void ResetButtons()
    {
        ButtonMap = new Dictionary<WheelButton, PadButton>();
        ShiftMap = new Dictionary<WheelButton, PadButton>();
        ShiftButton = null;
    }

    /// <summary>"C:\Games\Forza.exe" or "Forza" -> "forza.exe"; null if there's no usable name.</summary>
    public static string? NormalizeGame(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe))
            return null;
        var name = Path.GetFileName(exe.Trim()).ToLowerInvariant();
        if (name.Length == 0)
            return null;
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }

    /// <summary>
    /// Makes a profile from a file (settings.json or an import) safe to use: clamps
    /// every number to its slider's range, drops unknown buttons, and tidies names.
    /// </summary>
    public void Sanitize()
    {
        var defaults = new TuningProfile();
        static double Clamp(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

        Name = string.IsNullOrWhiteSpace(Name) ? defaults.Name : Name.Trim();
        if (Name.Length > MaxNameLength)
            Name = Name[..MaxNameLength].TrimEnd();

        SteeringSensitivity = Clamp(SteeringSensitivity, MinSensitivity, MaxSensitivity, defaults.SteeringSensitivity);
        SteeringDeadzone = Clamp(SteeringDeadzone, 0, MaxSteeringDeadzone, defaults.SteeringDeadzone);
        SteeringCurve = Clamp(SteeringCurve, MinCurve, MaxSteeringCurve, defaults.SteeringCurve);
        ThrottleDeadzone = Clamp(ThrottleDeadzone, 0, MaxPedalDeadzone, defaults.ThrottleDeadzone);
        BrakeDeadzone = Clamp(BrakeDeadzone, 0, MaxPedalDeadzone, defaults.BrakeDeadzone);
        ThrottleCurve = Clamp(ThrottleCurve, MinCurve, MaxPedalCurve, defaults.ThrottleCurve);
        BrakeCurve = Clamp(BrakeCurve, MinCurve, MaxPedalCurve, defaults.BrakeCurve);
        HapticIntensity = Clamp(HapticIntensity, 0, MaxHapticIntensity, defaults.HapticIntensity);

        // The string enum converter still accepts bare numbers, so out-of-range values can get this far.
        static Dictionary<WheelButton, PadButton> CleanMap(IReadOnlyDictionary<WheelButton, PadButton>? map) =>
            (map ?? new Dictionary<WheelButton, PadButton>())
                .Where(kv => Enum.IsDefined(kv.Key) && Enum.IsDefined(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        ButtonMap = CleanMap(ButtonMap);
        ShiftMap = CleanMap(ShiftMap);
        if (ShiftButton is { } shift && !Enum.IsDefined(shift))
            ShiftButton = null;

        Games = (Games ?? new List<string>())
            .Select(NormalizeGame)
            .OfType<string>()
            .Distinct()
            .ToList();
    }

    /// <summary>Dropdown items' accessible names (screen readers, UI automation) come from this.</summary>
    public override string ToString() => Name;

    /// <summary>
    /// Same settings under a new name. Linked games aren't copied, since a game
    /// can only switch to one profile. (The button maps are safe to share: they're
    /// never edited in place.)
    /// </summary>
    public TuningProfile CopyAs(string name)
    {
        var copy = (TuningProfile)MemberwiseClone();
        copy.Name = name;
        copy.Games = new List<string>();
        return copy;
    }

    /// <summary>Folds the legacy shared <see cref="PedalCurve"/> into the per-pedal curves.</summary>
    public void UpgradeLegacyFields()
    {
        if (PedalCurve is not double legacy)
            return;
        ThrottleCurve = legacy;
        BrakeCurve = legacy;
        PedalCurve = null;
    }
}
