using System.Numerics;

namespace TfmStudio.Core.Dsp;

/// <summary>Analytic signal and envelope via the FFT-based Hilbert transform.</summary>
public static class Hilbert
{
    /// <summary>
    /// Computes the analytic signal <c>x + i·H{x}</c> of a real signal. The input is zero-padded to
    /// twice the next power of two to suppress circular wrap-around.
    /// </summary>
    public static void Analytic(ReadOnlySpan<float> x, Span<float> real, Span<float> imag, Complex[]? workspace = null)
    {
        int n = x.Length;
        int m = FftPlan.NextPowerOfTwo(2 * n);
        var plan = FftPlan.Get(m);
        Complex[] buf = workspace is { Length: var len } && len == m ? workspace : new Complex[m];
        for (int i = 0; i < n; i++) buf[i] = new Complex(x[i], 0);
        for (int i = n; i < m; i++) buf[i] = Complex.Zero;
        plan.Forward(buf);
        // Keep DC and Nyquist, double positive frequencies, zero negative ones.
        for (int k = 1; k < m / 2; k++) buf[k] *= 2;
        for (int k = m / 2 + 1; k < m; k++) buf[k] = Complex.Zero;
        plan.Inverse(buf);
        for (int i = 0; i < n; i++)
        {
            real[i] = (float)buf[i].Real;
            imag[i] = (float)buf[i].Imaginary;
        }
    }

    /// <summary>Envelope |x + i·H{x}|.</summary>
    public static float[] Envelope(ReadOnlySpan<float> x)
    {
        var re = new float[x.Length];
        var im = new float[x.Length];
        Analytic(x, re, im);
        var env = new float[x.Length];
        for (int i = 0; i < env.Length; i++) env[i] = MathF.Sqrt(re[i] * re[i] + im[i] * im[i]);
        return env;
    }

    internal static int WorkspaceLength(int n) => FftPlan.NextPowerOfTwo(2 * n);
}
