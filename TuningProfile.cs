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

    /// <summary>Dropdown items' accessible names (screen readers, UI automation) come from this.</summary>
    public override string ToString() => Name;

    /// <summary>Same settings under a new name.</summary>
    public TuningProfile CopyAs(string name)
    {
        var copy = (TuningProfile)MemberwiseClone();
        copy.Name = name;
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
