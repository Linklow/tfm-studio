namespace TfmStudio.Core.Physics;

/// <summary>
/// Gaussian-modulated cosine pulse <c>p(t) = exp(-t²/2σ²)·cos(2π·fc·t)</c>, the standard broadband
/// model of a damped piezo-composite element. σ is derived from the -6 dB fractional bandwidth.
/// Evaluation uses an oversampled lookup table so that the simulator can place thousands of
/// echoes per A-scan at arbitrary (sub-sample) delays without calling exp/cos.
/// </summary>
public sealed class Pulse
{
    private const int Oversample = 64;
    private readonly float[] _table;
    private readonly double _invStep;
    private readonly double _halfDuration;

    public Pulse(double centerFrequency, double fractionalBandwidth)
    {
        if (centerFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(centerFrequency));
        if (fractionalBandwidth <= 0) throw new ArgumentOutOfRangeException(nameof(fractionalBandwidth));
        CenterFrequency = centerFrequency;
        FractionalBandwidth = fractionalBandwidth;
        double bandwidth = fractionalBandwidth * centerFrequency;
        double sigmaF = bandwidth / (2 * Math.Sqrt(2 * Math.Log(2)));
        Sigma = 1 / (2 * Math.PI * sigmaF);
        _halfDuration = 4 * Sigma;

        double step = 1 / (centerFrequency * Oversample);
        _invStep = 1 / step;
        int n = (int)Math.Ceiling(2 * _halfDuration / step) + 2;
        _table = new float[n];
        for (int i = 0; i < n; i++) _table[i] = (float)Evaluate(-_halfDuration + i * step);
    }

    public double CenterFrequency { get; }
    public double FractionalBandwidth { get; }

    /// <summary>Standard deviation of the Gaussian envelope, s.</summary>
    public double Sigma { get; }

    /// <summary>The pulse is treated as zero for |t| &gt; HalfDuration (= 4σ).</summary>
    public double HalfDuration => _halfDuration;

    public double Evaluate(double t) =>
        Math.Exp(-t * t / (2 * Sigma * Sigma)) * Math.Cos(2 * Math.PI * CenterFrequency * t);

    /// <summary>Fast table lookup with linear interpolation; zero outside ±HalfDuration.</summary>
    public float Lookup(double t)
    {
        double u = (t + _halfDuration) * _invStep;
        if (u < 0 || u >= _table.Length - 1) return 0f;
        int i = (int)u;
        float f = (float)(u - i);
        return _table[i] + f * (_table[i + 1] - _table[i]);
    }
}
