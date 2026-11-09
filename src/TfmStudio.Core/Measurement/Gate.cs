namespace TfmStudio.Core.Measurement;

/// <summary>How a gate reports time of flight.</summary>
public enum GateTrigger
{
    /// <summary>Time of the maximum amplitude inside the gate.</summary>
    Peak,
    /// <summary>Time of the first threshold crossing inside the gate.</summary>
    Flank,
}

/// <summary>Result of evaluating a gate on an A-scan.</summary>
/// <param name="Triggered">True if the signal crossed the gate threshold.</param>
/// <param name="Amplitude">Peak amplitude inside the gate (same units as the A-scan).</param>
/// <param name="Time">Time of flight according to the trigger mode, s.</param>
public readonly record struct GateResult(bool Triggered, double Amplitude, double Time);

/// <summary>
/// A time gate on a rectified A-scan or envelope, as on any ultrasonic flaw detector.
/// </summary>
/// <param name="Start">Gate start, s.</param>
/// <param name="Width">Gate width, s.</param>
/// <param name="Threshold">Threshold in the units of the signal.</param>
/// <param name="Trigger">Time-of-flight mode.</param>
public sealed record Gate(double Start, double Width, double Threshold, GateTrigger Trigger = GateTrigger.Peak)
{
    public double End => Start + Width;

    public GateResult Evaluate(ReadOnlySpan<float> signal, double samplingRate, double timeOffset = 0)
    {
        int i0 = Math.Max(0, (int)Math.Ceiling((Start - timeOffset) * samplingRate));
        int i1 = Math.Min(signal.Length - 1, (int)Math.Floor((End - timeOffset) * samplingRate));
        if (i1 < i0) return new GateResult(false, 0, double.NaN);
        int peak = i0, first = -1;
        float max = 0;
        for (int i = i0; i <= i1; i++)
        {
            float v = Math.Abs(signal[i]);
            if (v > max) { max = v; peak = i; }
            if (first < 0 && v >= Threshold) first = i;
        }
        bool triggered = first >= 0;
        double t = Trigger == GateTrigger.Peak || !triggered
            ? timeOffset + peak / samplingRate
            : timeOffset + Interpolate(signal, first, Threshold) / samplingRate;
        return new GateResult(triggered, max, triggered ? t : double.NaN);
    }

    private static double Interpolate(ReadOnlySpan<float> s, int i, double threshold)
    {
        if (i == 0) return 0;
        double a = Math.Abs(s[i - 1]), b = Math.Abs(s[i]);
        return b > a ? i - 1 + (threshold - a) / (b - a) : i;
    }

    /// <summary>Depth of a pulse-echo reflector from its time of flight at normal incidence.</summary>
    public static double Depth(double timeOfFlight, double velocity) => timeOfFlight * velocity / 2;
}
