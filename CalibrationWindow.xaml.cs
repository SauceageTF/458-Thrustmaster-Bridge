using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace WheelBridge;

/// <summary>
/// Walks the user through measuring their wheel's real center, end stops and
/// pedal travel, so full physical lock / a floored pedal map to 100% in-game.
/// Steps advance with the wheel's own A button (edge-triggered) as well as the
/// Next button, so the user doesn't have to let go of the wheel to click.
/// </summary>
public partial class CalibrationWindow : Window
{
    private enum Step { Intro, Center, Left, Right, Throttle, Brake, Done }

    // Movement the user must make before a capture counts, in raw units. Low
    // enough for a short-rotation bungee wheel, high enough to reject a nudge.
    private const int MinSteerTravel = 6000;
    private const int MinPedalTravel = 150;

    // While "at rest", readings must stay within these bands (spring settling).
    private const int RestSteerJitter = 1500;
    private const int RestPedalJitter = 40;
    private const int RestSampleCount = 25; // ~0.75 s at the 30 ms tick

    // Captured end points are pulled in by this much so full lock / full press
    // reliably reaches 100% instead of needing the exact peak every time.
    private const double EndMargin = 0.02;

    private const int PedalScaleMin = WheelSettings.DefaultPedalMaxRaw;

    private readonly WheelBridgeService _service;
    private readonly WheelSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly Queue<WheelState> _restSamples = new();

    private Step _step = Step.Intro;
    private bool _stepReady;
    private bool _prevA = true; // assume held so a press carried in from before the wizard doesn't count

    private int _center, _left, _right;
    private int _throttleRest, _throttleMax, _brakeRest, _brakeMax;

    public CalibrationWindow(WheelBridgeService service)
    {
        InitializeComponent();
        _service = service;
        _settings = service.Settings;

        foreach (var _ in Enum.GetValues<Step>())
            ProgressPips.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 4, 0) });

        EnterStep(Step.Intro);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Tick(_service.CurrentState);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void EnterStep(Step step)
    {
        _step = step;
        _restSamples.Clear();
        _stepReady = false;

        // Reset this step's capture, so Back -> redo starts fresh.
        switch (step)
        {
            case Step.Left: _left = _center; break;
            case Step.Right: _right = _center; break;
            case Step.Throttle: _throttleMax = _throttleRest; break;
            case Step.Brake: _brakeMax = _brakeRest; break;
        }

        var index = (int)step;
        var total = Enum.GetValues<Step>().Length;
        StepText.Text = step == Step.Done ? "ALL DONE" : $"STEP {index + 1} OF {total - 1}";
        for (var i = 0; i < ProgressPips.Children.Count; i++)
            ((Border)ProgressPips.Children[i]).Background = Brush(i <= index ? "AccentBrush" : "SurfaceAltBrush");

        (TitleText.Text, InstructionText.Text) = step switch
        {
            Step.Intro => ("Calibrate your wheel",
                "This measures your wheel's real center, how far it turns each way, and how far each pedal travels, " +
                "so full lock and a floored pedal reach 100% in-game.\n\n" +
                "Press A on the wheel to move to the next step, so your hands can stay on the wheel. (Next works too.)"),
            Step.Center => ("Let go of everything",
                "Let the wheel spring back to center and take your feet off both pedals. Keep still for a moment."),
            Step.Left => ("Full left",
                "Turn the wheel all the way to the LEFT. You can let it spring back afterwards, since the furthest point is remembered."),
            Step.Right => ("Full right",
                "Now turn it all the way to the RIGHT."),
            Step.Throttle => ("Throttle",
                "Press the throttle all the way to the floor, then let it up. The deepest point is remembered."),
            Step.Brake => ("Brake",
                "Press the brake all the way to the floor, then let it up."),
            _ => ("Calibration complete",
                "Here's what was measured. It applies to every profile. Save to start using it. " +
                "The current profile's steering sensitivity is reset to 1.0x, since full physical lock now maps to " +
                "full lock in-game. Raise it if you want full lock sooner."),
        };

        var steering = step is Step.Center or Step.Left or Step.Right;
        var pedal = step is Step.Throttle or Step.Brake;
        SteeringMeter.Visibility = SteeringLegend.Visibility = steering ? Visibility.Visible : Visibility.Collapsed;
        PedalMeter.Visibility = pedal ? Visibility.Visible : Visibility.Collapsed;
        PedalFill.Fill = Brush(step == Step.Brake ? "BadBrush" : "GoodBrush");
        LiveText.Visibility = steering || pedal ? Visibility.Visible : Visibility.Collapsed;
        StatusBox.Visibility = step == Step.Done ? Visibility.Collapsed : Visibility.Visible;
        SummaryText.Visibility = step == Step.Done ? Visibility.Visible : Visibility.Collapsed;
        if (step == Step.Done)
            SummaryText.Text = BuildSummary();

        BackButton.Visibility = step == Step.Intro ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = step switch
        {
            Step.Intro => "Start",
            Step.Done => "Save",
            _ => "Next",
        };
        UpdateReadiness(false, "");
    }

    private void Tick(WheelState state)
    {
        var live = state.WheelConnected && state.HasInput;
        var aPressed = live && state.A && !_prevA;
        _prevA = live && state.A;

        if (!live)
        {
            UpdateReadiness(_step == Step.Done, _step == Step.Done ? "" : "Waiting for the wheel... is it plugged in?");
            return;
        }

        switch (_step)
        {
            case Step.Intro:
                UpdateReadiness(true, "Wheel connected. Press A (or click Start) to begin.");
                break;

            case Step.Center:
                _restSamples.Enqueue(state);
                while (_restSamples.Count > RestSampleCount)
                    _restSamples.Dequeue();
                var full = _restSamples.Count == RestSampleCount;
                var still = full &&
                    Spread(_restSamples.Select(s => s.RawWheel)) <= RestSteerJitter &&
                    Spread(_restSamples.Select(s => s.RawThrottle)) <= RestPedalJitter &&
                    Spread(_restSamples.Select(s => s.RawBrake)) <= RestPedalJitter;
                if (still)
                {
                    _center = (int)_restSamples.Average(s => s.RawWheel);
                    // Highest resting reading, so a pedal that idles slightly off zero still reads 0%.
                    _throttleRest = _restSamples.Max(s => s.RawThrottle);
                    _brakeRest = _restSamples.Max(s => s.RawBrake);
                }
                UpdateReadiness(still, still ? "Steady. Press A to continue." : "Hold still...");
                RenderSteering(state.RawWheel, showCenter: still);
                break;

            case Step.Left:
                if (Math.Abs(state.RawWheel - _center) > Math.Abs(_left - _center))
                    _left = state.RawWheel;
                var leftOk = Math.Abs(_left - _center) >= MinSteerTravel;
                UpdateReadiness(leftOk, leftOk ? "Got it. Press A to continue." : "Keep turning left...");
                RenderSteering(state.RawWheel, showCenter: true);
                break;

            case Step.Right:
                // Only count travel on the opposite side of center from the left capture.
                var offset = state.RawWheel - _center;
                var oppositeSide = Math.Sign(offset) == -Math.Sign(_left - _center);
                if (oppositeSide && Math.Abs(offset) > Math.Abs(_right - _center))
                    _right = state.RawWheel;
                var rightOk = Math.Abs(_right - _center) >= MinSteerTravel;
                var wrongWay = !oppositeSide && Math.Abs(offset) >= MinSteerTravel;
                UpdateReadiness(rightOk,
                    rightOk ? "Got it. Press A to continue." :
                    wrongWay ? "That's the left side. Turn the other way." :
                    "Keep turning right...");
                RenderSteering(state.RawWheel, showCenter: true);
                break;

            case Step.Throttle:
                _throttleMax = Math.Max(_throttleMax, state.RawThrottle);
                var throttleOk = _throttleMax - _throttleRest >= MinPedalTravel;
                UpdateReadiness(throttleOk, throttleOk ? "Got it. Press A to continue." : "Press the throttle all the way down...");
                RenderPedal(state.RawThrottle, _throttleRest, _throttleMax, throttleOk);
                break;

            case Step.Brake:
                _brakeMax = Math.Max(_brakeMax, state.RawBrake);
                var brakeOk = _brakeMax - _brakeRest >= MinPedalTravel;
                UpdateReadiness(brakeOk, brakeOk ? "Got it. Press A to continue." : "Press the brake all the way down...");
                RenderPedal(state.RawBrake, _brakeRest, _brakeMax, brakeOk);
                break;

            case Step.Done:
                UpdateReadiness(true, "");
                break;
        }

        if (aPressed && _stepReady)
            Advance();
    }

    private void UpdateReadiness(bool ready, string message)
    {
        _stepReady = ready;
        NextButton.IsEnabled = ready;
        StatusText.Text = message;
        StatusDot.Fill = Brush(ready ? "GoodBrush" : "MutedTextBrush");
    }

    private void RenderSteering(int raw, bool showCenter)
    {
        var width = SteeringCanvas.ActualWidth;
        double X(int value) => Math.Clamp(value / 65535.0, 0, 1) * width;
        void Place(Rectangle mark, int value, bool visible)
        {
            mark.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(mark, X(value) - mark.Width / 2);
        }

        Place(SteerNeedle, raw, true);
        Place(SteerCenterMark, _center, showCenter);
        Place(SteerLeftMark, _left, _step >= Step.Left && _left != _center);
        Place(SteerRightMark, _right, _step >= Step.Right && _right != _center);

        // Shade the captured range so far, so the user sees it grow as they turn.
        var lo = Math.Min(_left, Math.Min(_right, _center));
        var hi = Math.Max(_left, Math.Max(_right, _center));
        SteerRangeFill.Visibility = _step >= Step.Left && hi > lo ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(SteerRangeFill, X(lo));
        SteerRangeFill.Width = Math.Max(0, X(hi) - X(lo));

        LiveText.Text = $"raw {raw,5}   center {(showCenter ? _center.ToString() : "-"),5}";
    }

    private void RenderPedal(int raw, int rest, int peak, bool peakValid)
    {
        var width = PedalCanvas.ActualWidth;
        var scale = (double)Math.Max(PedalScaleMin, peak);
        double X(int value) => Math.Clamp(value / scale, 0, 1) * width;

        PedalFill.Width = X(raw);
        Canvas.SetLeft(PedalRestMark, X(rest) - PedalRestMark.Width / 2);
        PedalPeakMark.Visibility = peakValid ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(PedalPeakMark, X(peak) - PedalPeakMark.Width / 2);

        LiveText.Text = $"raw {raw,4}   rest {rest,4}   deepest {peak,4}";
    }

    private string BuildSummary() =>
        $"Steering   left {_left}  ·  center {_center}  ·  right {_right}\n" +
        $"Throttle   rest {_throttleRest}  →  floored {_throttleMax}\n" +
        $"Brake      rest {_brakeRest}  →  floored {_brakeMax}";

    private void Advance()
    {
        if (_step == Step.Done)
        {
            Save();
            return;
        }
        EnterStep(_step + 1);
    }

    private void Save()
    {
        static int PullIn(int from, int to) => from + (int)Math.Round((to - from) * (1 - EndMargin));

        _settings.SteeringCenterRaw = _center;
        _settings.SteeringLeftRaw = PullIn(_center, _left);
        _settings.SteeringRightRaw = PullIn(_center, _right);
        _settings.ThrottleRestRaw = _throttleRest;
        _settings.ThrottleMaxRaw = PullIn(_throttleRest, _throttleMax);
        _settings.BrakeRestRaw = _brakeRest;
        _settings.BrakeMaxRaw = PullIn(_brakeRest, _brakeMax);
        _settings.Active.SteeringSensitivity = 1.0;
        _settings.IsCalibrated = true;
        _settings.Save();

        DialogResult = true;
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepReady)
            Advance();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step > Step.Intro)
            EnterStep(_step - 1);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static int Spread(IEnumerable<int> values)
    {
        var list = values.ToList();
        return list.Max() - list.Min();
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
}
