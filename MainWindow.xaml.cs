using System.Windows;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace WheelBridge;

public partial class MainWindow : Window
{
    private const double GaugeHalfWidth = 150;
    private const double PedalBarHeight = 62;

    private readonly WheelBridgeService _service;
    private readonly WheelSettings _settings;
    private readonly DispatcherTimer _timer;
    private bool _uiReady;

    public MainWindow(WheelBridgeService service)
    {
        InitializeComponent();
        _service = service;
        _settings = service.Settings;

        LoadSettingsIntoUi();
        _uiReady = true;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Render(_service.CurrentState);
        _timer.Start();
    }

    private void LoadSettingsIntoUi()
    {
        SensitivitySlider.Value = _settings.SteeringSensitivity;
        SteerDeadzoneSlider.Value = _settings.SteeringDeadzone;
        InvertSteeringCheck.IsChecked = _settings.InvertSteering;
        ThrottleDeadzoneSlider.Value = _settings.ThrottleDeadzone;
        BrakeDeadzoneSlider.Value = _settings.BrakeDeadzone;
        PedalCurveSlider.Value = _settings.PedalCurve;
        StartMinimizedCheck.IsChecked = _settings.StartMinimized;
        LaunchAtStartupCheck.IsChecked = _settings.LaunchAtWindowsStartup;
        UpdateSettingsLabels();
    }

    private void UpdateSettingsLabels()
    {
        SensitivityValueText.Text = $"{_settings.SteeringSensitivity:0.0}x";
        SteerDeadzoneValueText.Text = $"{_settings.SteeringDeadzone:P0}";
        ThrottleDeadzoneValueText.Text = $"{_settings.ThrottleDeadzone:P0}";
        BrakeDeadzoneValueText.Text = $"{_settings.BrakeDeadzone:P0}";
        PedalCurveValueText.Text = _settings.PedalCurve switch
        {
            < 0.9 => "sharp",
            > 1.1 => "gradual",
            _ => "linear",
        };
    }

    private void Render(WheelState state)
    {
        WheelDot.Fill = state.WheelConnected ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("MutedTextBrush");
        VigemDot.Fill = state.VigemConnected ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("MutedTextBrush");
        StatusMessageText.Text = state.StatusMessage ?? "";

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

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _settings.SteeringSensitivity = e.NewValue;
        UpdateSettingsLabels();
    }

    private void SteerDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _settings.SteeringDeadzone = e.NewValue;
        UpdateSettingsLabels();
    }

    private void ThrottleDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _settings.ThrottleDeadzone = e.NewValue;
        UpdateSettingsLabels();
    }

    private void BrakeDeadzoneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _settings.BrakeDeadzone = e.NewValue;
        UpdateSettingsLabels();
    }

    private void PedalCurveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _settings.PedalCurve = e.NewValue;
        UpdateSettingsLabels();
    }

    private void InvertSteeringCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.InvertSteering = InvertSteeringCheck.IsChecked == true;
    }

    private void AppOptionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        _settings.LaunchAtWindowsStartup = LaunchAtStartupCheck.IsChecked == true;
        WindowsStartup.SetEnabled(_settings.LaunchAtWindowsStartup);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        StatusMessageText.Text = "Settings saved.";
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        _uiReady = false;
        var defaults = new WheelSettings();
        _settings.SteeringSensitivity = defaults.SteeringSensitivity;
        _settings.SteeringDeadzone = defaults.SteeringDeadzone;
        _settings.InvertSteering = defaults.InvertSteering;
        _settings.ThrottleDeadzone = defaults.ThrottleDeadzone;
        _settings.BrakeDeadzone = defaults.BrakeDeadzone;
        _settings.PedalCurve = defaults.PedalCurve;
        _settings.StartMinimized = defaults.StartMinimized;
        _settings.LaunchAtWindowsStartup = defaults.LaunchAtWindowsStartup;
        LoadSettingsIntoUi();
        WindowsStartup.SetEnabled(_settings.LaunchAtWindowsStartup);
        _settings.Save();
        _uiReady = true;
    }
}
