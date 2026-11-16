using System.IO;
using System.Text.Json;

namespace WheelBridge;

/// <summary>
/// Reads and writes profiles as standalone JSON files, for sharing a game's setup
/// or moving it to another PC. An exported file looks like
/// <c>{ "WheelBridgeProfile": 1, "Profile": { ...TuningProfile... } }</c>.
/// Import also accepts a whole settings.json (every profile in it), or a bare
/// profile object.
/// </summary>
public static class ProfileFile
{
    public const string DialogFilter = "WheelBridge profile (*.json)|*.json|All files (*.*)|*.*";

    private const string FormatProperty = "WheelBridgeProfile";
    private const int FormatVersion = 1;
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static void Export(TuningProfile profile, string path)
    {
        var document = new Dictionary<string, object>
        {
            [FormatProperty] = FormatVersion,
            ["Profile"] = profile,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, WriteOptions));
    }

    /// <summary>A file name made from the profile's name, e.g. "Forza Horizon 5.json".</summary>
    public static string SuggestedFileName(TuningProfile profile)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(profile.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return (name.Length > 0 ? name : "profile") + ".json";
    }

    /// <summary>The profiles in <paramref name="path"/>. Throws <see cref="InvalidDataException"/> with a user-readable message if it isn't one.</summary>
    public static List<TuningProfile> Import(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new InvalidDataException("The file is too large to be a profile.");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("This isn't a WheelBridge profile file.");

            // Exported profile
            if (root.TryGetProperty("Profile", out var single) && single.ValueKind == JsonValueKind.Object)
                return new List<TuningProfile> { Read(single) };

            // A settings.json, copied from another PC
            if (root.TryGetProperty(nameof(WheelSettings.Profiles), out var many) && many.ValueKind == JsonValueKind.Array)
            {
                var profiles = many.EnumerateArray().Select(Read).ToList();
                if (profiles.Count == 0)
                    throw new InvalidDataException("That settings file has no profiles in it.");
                return profiles;
            }

            // A bare profile object; insist on at least one setting so random JSON isn't taken for defaults.
            if (root.TryGetProperty(nameof(TuningProfile.SteeringSensitivity), out _))
                return new List<TuningProfile> { Read(root) };

            throw new InvalidDataException("This isn't a WheelBridge profile file.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The file couldn't be read as a profile ({ex.Message}).", ex);
        }
    }

    private static TuningProfile Read(JsonElement element)
    {
        var profile = element.Deserialize<TuningProfile>() ?? throw new InvalidDataException("The profile in this file is empty.");
        profile.UpgradeLegacyFields();
        profile.Sanitize();
        return profile;
    }
}
