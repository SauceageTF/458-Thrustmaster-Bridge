using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace WheelBridge;

/// <summary>Outcome of asking GitHub for the latest release. <see cref="Error"/> is set when the check itself failed.</summary>
public sealed record UpdateCheckResult(bool UpdateAvailable, Version? LatestVersion, string? ReleaseUrl, string? Error);

/// <summary>
/// Compares this build's version against the newest published GitHub release.
/// Read-only: it never downloads or installs anything, it just tells the user
/// where the new installer is.
/// </summary>
public static class UpdateChecker
{
    private const string Repo = "SauceageTF/458-Thrustmaster-Bridge";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Repo}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";

    /// <summary>This build's version as major.minor.patch (from &lt;Version&gt; in the .csproj).</summary>
    public static Version CurrentVersion { get; } =
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    // Declared after CurrentVersion: static initializers run in order, and the client's User-Agent reads it.
    private static readonly HttpClient Http = CreateClient();

    public static string CurrentVersionText => $"v{CurrentVersion.ToString(3)}";

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, token);
            if (!response.IsSuccessStatusCode)
                return Failed($"GitHub returned {(int)response.StatusCode}");

            await using var body = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: token);
            var root = json.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return Failed($"Couldn't read the release version ({tag ?? "none"})");

            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            latest = Normalize(latest);
            return new UpdateCheckResult(latest > CurrentVersion, latest, url ?? ReleasesPage, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Offline, GitHub down, rate-limited, or the request timed out.
            return Failed("Couldn't reach GitHub");
        }
    }

    /// <summary>Opens a release page in the default browser.</summary>
    public static void OpenInBrowser(string? url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser registered; nothing useful to do.
        }
    }

    private static UpdateCheckResult Failed(string error) => new(false, null, null, error);

    // Tags are "v1.2.0" (3 parts) but assembly versions are 1.2.0.0 (4 parts), and
    // Version treats a missing part as lower than 0 -- compare on 3 parts only.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WheelBridge", CurrentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
