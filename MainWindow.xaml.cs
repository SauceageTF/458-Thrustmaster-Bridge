using System.Windows;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace WheelBridge;

public partial class MainWindow : Window
{
    private const double GaugeHalfWidth = 150;
    private const double PedalBarHeight = 62;

    private readonly WheelBridgeService _service;
    private readonly WheelSettings _settings;
    private readonly DispatcherTimer _timer;
    private bool _uiReady;
    private string? _updateUrl;

    /// <summary>The active profile; re-read every time, since switching profiles swaps the instance.</summary>
    private TuningProfile Tuning => _settings.Active;

    public MainWindow(WheelBridgeService service)
    {
        InitializeComponent();
        _service = service;
        _settings = service.Settings;

        // The graphs call straight into the same shaping code the bridge uses, so
        // what's drawn is exactly what the game gets (invert aside).
        SteeringGraph.Curve = x => AxisShaping.ShapeSteering(x, Tuning);
        SteeringGraph.Deadzone = () => Tuning.SteeringDeadzone;
        ThrottleGraph.Curve = x => AxisShaping.ShapePedal(x, Tuning.ThrottleDeadzone, Tuning.ThrottleCurve);
        ThrottleGraph.Deadzone = () => Tuning.ThrottleDeadzone;
        BrakeGraph.Curve = x => AxisShaping.ShapePedal(x, Tuning.BrakeDeadzone, Tuning.BrakeCurve);
        BrakeGraph.Deadzone = () => Tuning.BrakeDeadzone;

        LoadSettingsIntoUi();
        UpdateStatusText.Text = $"You have {UpdateChecker.CurrentVersionText}.";
        ProfileHintText.Text = ProfileHint;
        _uiReady = true;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Render(_service.CurrentState);
        _timer.Start();
    }

    private void LoadSettingsIntoUi()
    {
        RenderProfiles();
        SensitivitySlider.Value = Tuning.SteeringSensitivity;
        SteerDeadzoneSlider.Value = Tuning.SteeringDeadzone;
        SteerCurveSlider.Value = Tuning.SteeringCurve;
        InvertSteeringCheck.IsChecked = Tuning.InvertSteering;
        ThrottleDeadzoneSlider.Value = Tuning.ThrottleDeadzone;
        ThrottleCurveSlider.Value = Tuning.ThrottleCurve;
        BrakeDeadzoneSlider.Value = Tuning.BrakeDeadzone;
        BrakeCurveSlider.Value = Tuning.BrakeCurve;
        HapticXboxRadio.IsChecked = _settings.HapticTarget == HapticTarget.XboxController;
        HapticPicoRadio.IsChecked = _settings.HapticTarget == HapticTarget.PicoSerial;
        HapticOffRadio.IsChecked = _settings.HapticTarget == HapticTarget.Off;
        HapticPortBox.Text = _settings.HapticPort;
        HapticIntensitySlider.Value = Tuning.HapticIntensity;
        StartMinimizedCheck.IsChecked = _settings.StartMinimized;
        LaunchAtStartupCheck.IsChecked = _settings.LaunchAtWindowsStartup;
        CheckUpdatesCheck.IsChecked = _settings.CheckForUpdatesAtStartup;
        UpdateSettingsLabels();
        RenderCalibrationStatus();
    }

    private void UpdateSettingsLabels()
    {
        SensitivityValueText.Text = $"{Tuning.SteeringSensitivity:0.0}x";
        SteerDeadzoneValueText.Text = $"{Tuning.SteeringDeadzone:P0}";
        ThrottleDeadzoneValueText.Text = $"{Tuning.ThrottleDeadzone:P0}";
        BrakeDeadzoneValueText.Text = $"{Tuning.BrakeDeadzone:P0}";
        SteerCurveValueText.Text = CurveLabel(Tuning.SteeringCurve);
        ThrottleCurveValueText.Text = CurveLabel(Tuning.ThrottleCurve);
        BrakeCurveValueText.Text = CurveLabel(Tuning.BrakeCurve);
        HapticIntensityValueText.Text = $"{Tuning.HapticIntensity:P0}";

        // Every tuning change reshapes a curve; repaint so the graphs follow the slider.
        SteeringGraph.InvalidateVisual();
        ThrottleGraph.InvalidateVisual();
        BrakeGraph.InvalidateVisual();
    }

    private static string CurveLabel(double exponent) =>
        Math.Abs(exponent - 1.0) < 0.001 ? "linear" : exponent.ToString("0.00");

    private void RenderCalibrationStatus()
    {
        if (_settings.IsCalibrated)
        {
            CalibrationDot.Fill = (Brush)FindResource("GoodBrush");
            CalibrationStatusText.Text = "Calibrated";
            CalibrationDetailText.Text = "Using your wheel's measured center, lock and pedal travel.";
        }
        else
        {
            CalibrationDot.Fill = (Brush)FindResource("AccentBrush");
            CalibrationStatusText.Text = "Not calibrated";
            CalibrationDetailText.Text = "Using factory ranges, so full lock or a floored pedal may not reach 100%.";
        }
        ResetCalibrationButton.IsEnabled = _settings.IsCalibrated;
    }

    private void Render(WheelState state)
    {
        WheelDot.Fill = state.WheelConnected ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("MutedTextBrush");
        VigemDot.Fill = state.VigemConnected ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("MutedTextBrush");
        StatusMessageText.Text = state.StatusMessage ?? "";

        var haptics = _service.Haptics.CurrentState;
        HapticDot.Fill = haptics.Connected ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("MutedTextBrush");
        HapticBadgeText.Text = haptics.Connected ? $"RUMBLE → {haptics.TargetName}" : "RUMBLE";
        RenderRumbleBar(RumbleLargeFill, RumbleLargeText, "LARGE", haptics.LargeMotor);
        RenderRumbleBar(RumbleSmallFill, RumbleSmallText, "SMALL", haptics.SmallMotor);

        // Live dots on the curve graphs (hidden until real input frames arrive).
        var live = state.WheelConnected && state.HasInput;
        SteeringGraph.LiveInput = live ? state.WheelInput : null;
        ThrottleGraph.LiveInput = live ? state.ThrottleInput : null;
        BrakeGraph.LiveInput = live ? state.BrakeInput : null;

        SteerValueText.Text = state.Wheel.ToString("0.00");
        if (state.Wheel < 0)
        {
            SteerLeftFill.Width = Math.Min(GaugeHalfWidth, -state.Wheel * GaugeHalfWidth);
            SteerRightFill.Width = 0;
        }
        else
        {
            SteerRightFill.Width = Math.Min(GaugeHalfWidth, state.Wheel * GaugeHalfWidth);
            SteerLeftFill.Width = 0;
        }

        ThrottleFill.Height = state.Throttle * PedalBarHeight;
        ThrottleValueText.Text = state.Throttle.ToString("P0");
        BrakeFill.Height = state.Brake * PedalBarHeight;
        BrakeValueText.Text = state.Brake.ToString("P0");

        SetChip(ChipA, state.A);
        SetChip(ChipB, state.B);
        SetChip(ChipX, state.X);
        SetChip(ChipY, state.Y);
        SetChip(ChipLB, state.LeftShoulder);
        SetChip(ChipRB, state.RightShoulder);
        SetChip(ChipStart, state.Start);
        SetChip(ChipBack, state.Back);
        SetChip(ChipUp, state.Up);
        SetChip(ChipDown, state.Down);
        SetChip(ChipLeft, state.Left);
        SetChip(ChipRight, state.Right);
    }

    private void SetChip(System.Windows.Controls.Border chip, bool active) =>
        chip.Background = active ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("SurfaceBrush");

    private static void RenderRumbleBar(System.Windows.Shapes.Rectangle fill, System.Windows.Controls.TextBlock label, string name, byte value)
    {
        var fraction = value / 255.0;
        fill.Width = ((FrameworkElement)fill.Parent).ActualWidth * fraction;
        label.Text = $"{name} {fraction:P0}";
    }

    private void HapticTargetRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.HapticTarget =
            HapticXboxRadio.IsChecked == true ? HapticTarget.XboxController :
            HapticPicoRadio.IsChecked == true ? HapticTarget.PicoSerial :
            HapticTarget.Off;
    }

    private void HapticPortBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.HapticPort = HapticPortBox.Text.Trim();
    }

    private void HapticIntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        Tuning.HapticIntensity = e.NewValue;
        UpdateSettingsLabels();
    }

    private void TestHapticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.Haptics.CurrentState.Connected)
        {
            Flash(TestHapticsFeedbackText, "No rumble output connected. Check the RUMBLE OUTPUT choice above.");
            return;
        }
        _service.Haptics.Pulse(TimeSpan.FromSeconds(1));
    }

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        Tuning.SteeringSensitivity = e.NewValue;
        UpdateSettingsLabels();
    }

    private void SteerDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        Tuning.SteeringDeadzone = e.NewValue;
        UpdateSettingsLabels();
    }

    private void ThrottleDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        Tuning.ThrottleDeadzone = e.NewValue;
        UpdateSettingsLabels();
    }

    private void BrakeDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        Tuning.BrakeDeadzone = e.NewValue;
        UpdateSettingsLabels();
    }

    private void SteerCurveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        Tuning.SteeringCurve = e.NewValue;
        UpdateSettingsLabels();
    }

    private void ThrottleCurveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        Tuning.ThrottleCurve = e.NewValue;
        UpdateSettingsLabels();
    }

    private void BrakeCurveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        Tuning.BrakeCurve = e.NewValue;
        UpdateSettingsLabels();
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new CalibrationWindow(_service) { Owner = this };
        if (wizard.ShowDialog() != true)
            return;

        // The wizard saved calibration and reset sensitivity; reflect both here.
        _uiReady = false;
        LoadSettingsIntoUi();
        _uiReady = true;
    }

    private void ResetCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Forget the measured wheel and pedal ranges and go back to factory ranges?",
            "Reset calibration", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        _settings.ResetCalibration();
        _settings.Save();
        RenderCalibrationStatus();
        UpdateSettingsLabels();
    }

    /// <summary>Shows the "update available" banner on the STATUS tab.</summary>
    public void ShowUpdateAvailable(UpdateCheckResult result)
    {
        _updateUrl = result.ReleaseUrl;
        UpdateBannerTitle.Text = $"Update available: v{result.LatestVersion?.ToString(3)}";
        UpdateBannerDetail.Text = $"You have {UpdateChecker.CurrentVersionText}. Download the new installer from GitHub.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking...";
        var result = await UpdateChecker.CheckAsync();
        CheckUpdatesButton.IsEnabled = true;

        if (result.Error is not null)
        {
            UpdateStatusText.Text = $"{result.Error}. Try again later.";
        }
        else if (result.UpdateAvailable)
        {
            ShowUpdateAvailable(result);
            UpdateStatusText.Text = $"v{result.LatestVersion?.ToString(3)} is available (see the STATUS tab).";
        }
        else
        {
            UpdateStatusText.Text = $"You're up to date ({UpdateChecker.CurrentVersionText}).";
        }
    }

    private void DownloadUpdateButton_Click(object sender, RoutedEventArgs e) => UpdateChecker.OpenInBrowser(_updateUrl);

    private void DismissUpdateButton_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private void InvertSteeringCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        Tuning.InvertSteering = InvertSteeringCheck.IsChecked == true;
    }

    private void AppOptionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        _settings.LaunchAtWindowsStartup = LaunchAtStartupCheck.IsChecked == true;
        _settings.CheckForUpdatesAtStartup = CheckUpdatesCheck.IsChecked == true;
        WindowsStartup.SetEnabled(_settings.LaunchAtWindowsStartup);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        Flash(SaveFeedbackText, "✓ Saved");
    }

    /// <summary>
    /// Shows a short-lived message next to the button that caused it, then clears
    /// it. (The STATUS tab's message line is rewritten from the bridge 20x a second,
    /// and isn't visible from SETTINGS anyway.)
    /// </summary>
    private static void Flash(System.Windows.Controls.TextBlock target, string message)
    {
        // A second click restarts the countdown rather than stacking timers.
        (target.Tag as DispatcherTimer)?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            target.Text = "";
        };
        target.Tag = timer;
        target.Text = message;
        timer.Start();
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            $"Reset the \"{Tuning.Name}\" profile and the shared rumble and app options to defaults?\n\n" +
            "Other profiles and your calibration are kept.",
            "Restore defaults", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        _uiReady = false;
        var defaults = new WheelSettings();
        _settings.ResetActiveProfile();
        _settings.HapticTarget = defaults.HapticTarget;
        _settings.HapticPort = defaults.HapticPort;
        _settings.StartMinimized = defaults.StartMinimized;
        _settings.LaunchAtWindowsStartup = defaults.LaunchAtWindowsStartup;
        _settings.CheckForUpdatesAtStartup = defaults.CheckForUpdatesAtStartup;
        // Calibration is deliberately kept: it describes this wheel, not a preference.
        LoadSettingsIntoUi();
        WindowsStartup.SetEnabled(_settings.LaunchAtWindowsStartup);
        _settings.Save();
        _uiReady = true;
    }

    // --- Profiles -------------------------------------------------------------

    private const string ProfileHint =
        "Steering, pedal and rumble-intensity settings are saved per profile. Calibration, rumble output and app options are shared.";

    /// <summary>Fills the tab-bar dropdown and the SETTINGS profile name. Callers guard with _uiReady.</summary>
    private void RenderProfiles()
    {
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = _settings.Profiles;
        ProfileCombo.SelectedItem = Tuning;
        ProfileNameText.Text = Tuning.Name;
        DeleteProfileButton.IsEnabled = _settings.Profiles.Count > 1;
    }

    /// <summary>
    /// Makes <paramref name="profile"/> active and saves straight away, so tweaks
    /// made to the profile being left aren't lost. Also used by the tray menu.
    /// </summary>
    public void SwitchProfile(TuningProfile profile)
    {
        if (profile == Tuning)
            return;

        // A rename in progress belongs to the profile being left; drop it rather than misapply it.
        EndRename(commit: false);
        _settings.SwitchTo(profile);
        _settings.Save();
        _uiReady = false;
        LoadSettingsIntoUi();
        _uiReady = true;
    }

    private void ProfileCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_uiReady || ProfileCombo.SelectedItem is not TuningProfile profile)
            return;
        SwitchProfile(profile);
    }

    private void AddProfileButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.AddProfile();
        _settings.Save();
        _uiReady = false;
        LoadSettingsIntoUi();
        _uiReady = true;

        // Straight into naming it; Enter keeps the default "Profile N".
        BeginRename();
    }

    private void RenameProfileButton_Click(object sender, RoutedEventArgs e) => BeginRename();

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = Tuning;
        var answer = MessageBox.Show(this,
            $"Delete the \"{profile.Name}\" profile? Its settings will be lost.",
            "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes || !_settings.RemoveProfile(profile))
            return;

        _settings.Save();
        _uiReady = false;
        LoadSettingsIntoUi();
        _uiReady = true;
    }

    private void BeginRename()
    {
        ProfileNameBox.Text = Tuning.Name;
        ProfileNamePanel.Visibility = Visibility.Collapsed;
        ProfileNameBox.Visibility = Visibility.Visible;
        AddProfileButton.IsEnabled = RenameProfileButton.IsEnabled = DeleteProfileButton.IsEnabled = false;
        ProfileHintText.Text = "Type a name, then press Enter (Esc to cancel).";
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
    }

    /// <param name="keepEditingIfInvalid">Enter on a bad name keeps the box open to fix it; clicking away just cancels.</param>
    private void EndRename(bool commit, bool keepEditingIfInvalid = false)
    {
        if (ProfileNameBox.Visibility != Visibility.Visible)
            return;

        if (commit)
        {
            var name = ProfileNameBox.Text.Trim();
            var valid = name.Length > 0 && !_settings.IsProfileNameTaken(name, except: Tuning);
            if (!valid && keepEditingIfInvalid)
            {
                ProfileHintText.Text = name.Length == 0
                    ? "The name can't be empty."
                    : $"There's already a profile called \"{name}\".";
                return;
            }
            if (valid)
            {
                Tuning.Name = name;
                _settings.Save();
            }
        }

        ProfileNameBox.Visibility = Visibility.Collapsed;
        ProfileNamePanel.Visibility = Visibility.Visible;
        AddProfileButton.IsEnabled = RenameProfileButton.IsEnabled = true;
        ProfileHintText.Text = ProfileHint;
        _uiReady = false;
        RenderProfiles();
        _uiReady = true;
    }

    private void ProfileNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            EndRename(commit: true, keepEditingIfInvalid: true);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            EndRename(commit: false);
            e.Handled = true;
        }
    }

    // Clicking elsewhere keeps what was typed, like most rename boxes.
    private void ProfileNameBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        EndRename(commit: true);
}
