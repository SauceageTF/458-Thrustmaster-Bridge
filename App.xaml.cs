using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace WheelBridge;

public partial class App : Application
{
    // Fixed GUID so the mutex name is stable across rebuilds/versions.
    private const string SingleInstanceMutexName = "WheelBridge-458Spider-9F3B2C7E-SingleInstance";

    private WheelBridgeService? _service;
    private NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private string? _updateUrl;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // We don't own the mutex in this case (someone else does) -- just close our
            // handle to it, don't ReleaseMutex (that would throw: we never acquired it).
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;

            System.Windows.MessageBox.Show(
                "458 Spider Bridge is already running -- check the system tray.",
                "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _service = new WheelBridgeService();
        var settings = WheelSettings.Load();
        _service.Settings = settings;
        WindowsStartup.SetEnabled(settings.LaunchAtWindowsStartup);

        var exePath = Environment.ProcessPath;
        var appIcon = !string.IsNullOrEmpty(exePath) ? Icon.ExtractAssociatedIcon(exePath) : SystemIcons.Application;

        _trayIcon = new NotifyIcon
        {
            Icon = appIcon,
            Visible = true,
            Text = "458 Spider Bridge",
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        _trayIcon.BalloonTipClicked += (_, _) => UpdateChecker.OpenInBrowser(_updateUrl);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show status", null, (_, _) => ShowMainWindow());
        var profilesMenu = new ToolStripMenuItem("Profile");
        menu.Items.Add(profilesMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        // Rebuilt on every open, so it reflects profiles added/renamed since.
        menu.Opening += (_, _) => FillProfilesMenu(profilesMenu, settings);
        _trayIcon.ContextMenuStrip = menu;

        _mainWindow = new MainWindow(_service);
        _mainWindow.Closing += (_, args) =>
        {
            // Closing the window just hides it -- the bridge keeps running in
            // the tray so the virtual controller stays connected while gaming.
            args.Cancel = true;
            _mainWindow.Hide();
        };
        if (!settings.StartMinimized)
            _mainWindow.Show();

        _service.Start();

        if (settings.CheckForUpdatesAtStartup)
            _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// One background check against GitHub releases. Silent unless there's an
    /// update: a banner in the window, plus a tray notification if the window is
    /// hidden (started minimized), since that's the only place the user would see it.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        var result = await UpdateChecker.CheckAsync();
        if (!result.UpdateAvailable || _mainWindow is null || _trayIcon is null)
            return;

        _updateUrl = result.ReleaseUrl;
        _mainWindow.ShowUpdateAvailable(result);
        if (!_mainWindow.IsVisible)
        {
            _trayIcon.ShowBalloonTip(10000, "Update available",
                $"458 Spider Bridge v{result.LatestVersion?.ToString(3)} is out (you have {UpdateChecker.CurrentVersionText}). Click to download.",
                ToolTipIcon.Info);
        }
    }

    /// <summary>One checkable entry per profile, so switching between games doesn't need the window.</summary>
    private void FillProfilesMenu(ToolStripMenuItem profilesMenu, WheelSettings settings)
    {
        profilesMenu.DropDownItems.Clear();
        foreach (var profile in settings.Profiles)
        {
            var item = new ToolStripMenuItem(profile.Name) { Checked = profile == settings.Active };
            item.Click += (_, _) => _mainWindow?.SwitchProfile(profile);
            profilesMenu.DropDownItems.Add(item);
        }
    }

    private void ShowMainWindow()
    {
        _mainWindow!.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        _trayIcon!.Visible = false;
        _service?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _service?.Dispose();
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
