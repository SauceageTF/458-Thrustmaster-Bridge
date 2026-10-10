using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WheelBridge;

/// <summary>
/// Everything persisted to settings.json: the per-game <see cref="Profiles"/>
/// (feel/tuning), plus settings shared by all profiles because they describe the
/// hardware or the app rather than a preference -- calibration, where rumble
/// goes, and startup options. Defaults assume a bungee-return wheel: no force
/// feedback motor, spring-centered, and often a shorter physical rotation range
/// than games expect.
/// </summary>
public sealed class WheelSettings
{
    private List<TuningProfile> _profiles = new() { new TuningProfile() };
    private volatile TuningProfile _active;
    private int _loadedActiveIndex;

    public WheelSettings() => _active = _profiles[0];

    /// <summary>Never empty. Only touched from the UI thread; the bridge threads read <see cref="Active"/> instead.</summary>
    public List<TuningProfile> Profiles
    {
        get => _profiles;
        set => _profiles = value; // from JSON; Load() re-points Active afterwards
    }

    /// <summary>Which profile is in use, for persistence only.</summary>
    public int ActiveProfileIndex
    {
        get => Math.Max(0, _profiles.IndexOf(_active));
        set => _loadedActiveIndex = value;
    }

    /// <summary>
    /// The profile the bridge is applying right now. Switching swaps this one
    /// reference, so the bridge thread sees either the old profile or the new
    /// one, never a mix of the two.
    /// </summary>
    [JsonIgnore]
    public TuningProfile Active => _active;

    // Calibration: raw USB values at the wheel's/pedals' physical limits, captured
    // by the calibration wizard. Defaults are the device's nominal full range.

    public bool IsCalibrated { get; set; }

    public int SteeringCenterRaw { get; set; } = DefaultSteeringCenterRaw;
    public int SteeringLeftRaw { get; set; } = DefaultSteeringLeftRaw;
    public int SteeringRightRaw { get; set; } = DefaultSteeringRightRaw;
    public int ThrottleRestRaw { get; set; } = DefaultPedalRestRaw;
    public int ThrottleMaxRaw { get; set; } = DefaultPedalMaxRaw;
    public int BrakeRestRaw { get; set; } = DefaultPedalRestRaw;
    public int BrakeMaxRaw { get; set; } = DefaultPedalMaxRaw;

    public const int DefaultSteeringCenterRaw = 0x8000;
    public const int DefaultSteeringLeftRaw = 0;
    public const int DefaultSteeringRightRaw = 0xFFFF;
    public const int DefaultPedalRestRaw = 0;
    public const int DefaultPedalMaxRaw = 0x3FF;

    /// <summary>Where the game's rumble is sent. Defaults to a plugged-in Xbox controller since that needs no extra parts.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HapticTarget HapticTarget { get; set; } = HapticTarget.XboxController;

    /// <summary>
    /// For <see cref="HapticTarget.PicoSerial"/>: "auto" to find the Pico by USB
    /// vendor ID, or a specific "COMn".
    /// </summary>
    public string HapticPort { get; set; } = HapticOutput.AutoPort;

    public bool StartMinimized { get; set; }

    public bool LaunchAtWindowsStartup { get; set; }

    /// <summary>Ask GitHub for a newer release once at startup. Only reads the public releases API; nothing is downloaded automatically.</summary>
    public bool CheckForUpdatesAtStartup { get; set; } = true;

    public static string DefaultProfileName(int number) => $"Profile {number}";

    /// <summary>"Profile N" for the first N past the current count that isn't already taken.</summary>
    public string NextDefaultProfileName()
    {
        for (var n = _profiles.Count + 1; ; n++)
        {
            var name = DefaultProfileName(n);
            if (!IsProfileNameTaken(name))
                return name;
        }
    }

    public bool IsProfileNameTaken(string name, TuningProfile? except = null) =>
        _profiles.Any(p => p != except && string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void SwitchTo(TuningProfile profile)
    {
        if (_profiles.Contains(profile))
            _active = profile;
    }

    /// <summary>Adds a profile starting from the current one's settings, and switches to it.</summary>
    public TuningProfile AddProfile()
    {
        var profile = _active.CopyAs(NextDefaultProfileName());
        _profiles.Add(profile);
        _active = profile;
        return profile;
    }

    /// <summary>Removes a profile, unless it's the last one. Deleting the active profile switches to its neighbour.</summary>
    public bool RemoveProfile(TuningProfile profile)
    {
        var index = _profiles.IndexOf(profile);
        if (index < 0 || _profiles.Count <= 1)
            return false;

        if (_active == profile)
            _active = _profiles[index + 1 < _profiles.Count ? index + 1 : index - 1];
        _profiles.RemoveAt(index);
        return true;
    }

    /// <summary>Puts the active profile's tuning back to defaults, keeping its name.</summary>
    public void ResetActiveProfile()
    {
        var index = _profiles.IndexOf(_active);
        var fresh = new TuningProfile { Name = _active.Name };
        _profiles[index] = fresh;
        _active = fresh;
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WheelBridge", "settings.json");

    public static WheelSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<WheelSettings>(json);
                if (loaded is not null)
                {
                    loaded.AfterLoad(json);
                    return loaded;
                }
            }
        }
        catch
        {
            // fall through to defaults on any read/parse failure
        }

        return new WheelSettings();
    }

    internal void AfterLoad(string json)
    {
        // Before profiles existed, the tuning sat at the top level of the file under
        // the same property names TuningProfile uses -- lift it into "Profile 1".
        using (var doc = JsonDocument.Parse(json))
        {
            if (!doc.RootElement.TryGetProperty(nameof(Profiles), out _))
                _profiles = new List<TuningProfile> { JsonSerializer.Deserialize<TuningProfile>(json) ?? new TuningProfile() };
        }

        if (_profiles.Count == 0)
            _profiles.Add(new TuningProfile());
        foreach (var profile in _profiles)
            profile.UpgradeLegacyFields();
        _active = _profiles[Math.Clamp(_loadedActiveIndex, 0, _profiles.Count - 1)];
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Forget the calibration wizard's results and go back to the device's nominal range.</summary>
    public void ResetCalibration()
    {
        IsCalibrated = false;
        SteeringCenterRaw = DefaultSteeringCenterRaw;
        SteeringLeftRaw = DefaultSteeringLeftRaw;
        SteeringRightRaw = DefaultSteeringRightRaw;
        ThrottleRestRaw = DefaultPedalRestRaw;
        ThrottleMaxRaw = DefaultPedalMaxRaw;
        BrakeRestRaw = DefaultPedalRestRaw;
        BrakeMaxRaw = DefaultPedalMaxRaw;
    }
}
