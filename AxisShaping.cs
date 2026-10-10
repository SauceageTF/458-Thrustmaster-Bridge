namespace WheelBridge;

/// <summary>
/// Raw wheel/pedal readings -> what the game sees. Split into two stages so the
/// curve graphs in the UI can plot exactly what the bridge does:
/// <list type="bullet">
/// <item>Normalize: raw USB value -> physical position, using the calibration
/// (steering -1..1, pedals 0..1). This is where the controller physically is.</item>
/// <item>Shape: physical position -> output, using deadzone, sensitivity and curve.</item>
/// </list>
/// </summary>
public static class AxisShaping
{
    /// <summary>Raw wheel position -> -1 (full left) .. 1 (full right), per the calibrated center and end stops.</summary>
    public static double NormalizeSteering(int raw, WheelSettings s)
    {
        var offset = raw - s.SteeringCenterRaw;
        if (offset == 0)
            return 0;

        // Measure each half against its own end stop: bungee wheels are rarely
        // symmetric. Comparing signs (rather than assuming left < center) also
        // copes with a wheel whose raw axis runs the other way.
        var leftSpan = s.SteeringLeftRaw - s.SteeringCenterRaw;
        var rightSpan = s.SteeringRightRaw - s.SteeringCenterRaw;
        var value = Math.Sign(offset) == Math.Sign(leftSpan)
            ? -SafeDivide(offset, leftSpan)
            : SafeDivide(offset, rightSpan);
        return Math.Clamp(value, -1.0, 1.0);
    }

    /// <summary>
    /// Physical steering (-1..1) -> output (-1..1): center deadzone, then
    /// sensitivity, then response curve. The deadzone is rescaled so output
    /// ramps up from zero at its edge instead of jumping.
    /// </summary>
    public static double ShapeSteering(double physical, TuningProfile s)
    {
        var magnitude = RemoveDeadzone(Math.Abs(physical), s.SteeringDeadzone);
        magnitude = Math.Min(1.0, magnitude * s.SteeringSensitivity);
        return Math.Sign(physical) * ApplyCurve(magnitude, s.SteeringCurve);
    }

    /// <summary>Raw pedal value -> 0 (resting) .. 1 (floored), per the calibrated range.</summary>
    public static double NormalizePedal(int raw, int restRaw, int maxRaw) =>
        Math.Clamp(SafeDivide(raw - restRaw, maxRaw - restRaw), 0.0, 1.0);

    /// <summary>Physical pedal (0..1) -> output (0..1): resting deadzone (rescaled), then curve.</summary>
    public static double ShapePedal(double physical, double deadzone, double curve) =>
        ApplyCurve(RemoveDeadzone(physical, deadzone), curve);

    private static double RemoveDeadzone(double value01, double deadzone)
    {
        value01 = Math.Clamp(value01, 0.0, 1.0);
        if (value01 <= deadzone)
            return 0.0;
        return (value01 - deadzone) / (1.0 - deadzone);
    }

    /// <summary>Power curve: 1 = linear, &lt;1 = quicker off the start, &gt;1 = gentler off the start.</summary>
    private static double ApplyCurve(double value01, double exponent) =>
        Math.Clamp(Math.Pow(value01, exponent), 0.0, 1.0);

    private static double SafeDivide(double numerator, double denominator) =>
        denominator == 0 ? 0 : numerator / denominator;
}
