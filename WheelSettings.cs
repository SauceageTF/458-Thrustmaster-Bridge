using System.IO;
using System.Text.Json;

namespace WheelBridge;

/// <summary>
/// User-adjustable tuning. Defaults assume a bungee-return wheel: no force
/// feedback motor, spring-centered, and often a shorter physical rotation
/// range than games expect -- hence sensitivity/deadzone rather than any FFB
/// settings.
/// </summary>
public sealed class WheelSettings
{
    /// <summary>Multiplies wheel deflection. &gt;1 amplifies a bungee wheel's short rotation range to reach full lock in-game.</summary>
    public double SteeringSensitivity { get; set; } = 1.0;

    /// <summary>Fraction of travel near center ignored, to absorb bungee spring slack/jitter at rest.</summary>
    public double SteeringDeadzone { get; set; } = 0.03;

    public bool InvertSteering { get; set; }

    /// <summary>Fraction of pedal travel near the resting position ignored.</summary>
    public double ThrottleDeadzone { get; set; } = 0.02;

    public double BrakeDeadzone { get; set; } = 0.02;

    /// <summary>Pedal response curve: 1.0 = linear, &lt;1 = more sensitive near the top, &gt;1 = more gradual initial travel.</summary>
    public double PedalCurve { get; set; } = 1.0;

    public bool StartMinimized { get; set; }

    public bool LaunchAtWindowsStartup { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WheelBridge", "settings.json");

    public static WheelSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<WheelSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // fall through to defaults on any read/parse failure
        }

        return new WheelSettings();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public WheelSettings Clone() => (WheelSettings)MemberwiseClone();
}
