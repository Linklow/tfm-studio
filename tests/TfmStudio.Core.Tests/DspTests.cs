using System.Numerics;
using TfmStudio.Core.Dsp;
using TfmStudio.Core.Physics;

namespace TfmStudio.Core.Tests;

public class DspTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(1024)]
    public void Fft_matches_naive_dft_and_round_trips(int n)
    {
        var rng = new Random(n);
        var x = Enumerable.Range(0, n).Select(_ => new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)).ToArray();
        var y = (Complex[])x.Clone();
        var plan = FftPlan.Get(n);
        plan.Forward(y);
        for (int k = 0; k < n; k += Math.Max(1, n / 16))
        {
            Complex sum = 0;
            for (int t = 0; t < n; t++) sum += x[t] * Complex.FromPolarCoordinates(1, -2 * Math.PI * k * t / n);
            Assert.True((sum - y[k]).Magnitude < 1e-9 * n, $"bin {k}");
        }
        plan.Inverse(y);
        for (int t = 0; t < n; t++) Assert.True((x[t] - y[t]).Magnitude < 1e-12);
    }

    [Fact]
    public void Fft_rejects_non_power_of_two() => Assert.Throws<ArgumentException>(() => FftPlan.Get(100));

    [Fact]
    public void Hilbert_envelope_recovers_gaussian_envelope()
    {
        var pulse = new Pulse(5e6, 0.6);
        double fs = 100e6;
        int n = 512;
        double t0 = n / 2 / fs;
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)pulse.Evaluate(i / fs - t0);
        var env = Hilbert.Envelope(x);
        for (int i = 0; i < n; i++)
        {
            double t = i / fs - t0;
            double expected = Math.Exp(-t * t / (2 * pulse.Sigma * pulse.Sigma));
            Assert.True(Math.Abs(env[i] - expected) < 0.02, $"sample {i}: {env[i]} vs {expected}");
        }
    }

    [Fact]
    public void Analytic_signal_of_cosine_has_sine_quadrature()
    {
        int n = 256;
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = MathF.Cos(2 * MathF.PI * 8 * i / n);
        var re = new float[n];
        var im = new float[n];
        Hilbert.Analytic(x, re, im);
        // Away from the ends (zero padding makes the edges non-periodic) H{cos} = sin.
        for (int i = 64; i < 192; i++) Assert.InRange(im[i] - MathF.Sin(2 * MathF.PI * 8 * i / n), -0.05f, 0.05f);
    }

    [Fact]
    public void Pulse_lookup_matches_closed_form_and_bandwidth_definition()
    {
        var pulse = new Pulse(5e6, 0.6);
        for (double t = -pulse.HalfDuration; t <= pulse.HalfDuration; t += 1.37e-9)
            Assert.True(Math.Abs(pulse.Lookup(t) - pulse.Evaluate(t)) < 2e-3);
        Assert.Equal(0f, pulse.Lookup(pulse.HalfDuration * 1.01));
        // -6 dB bandwidth 3 MHz -> sigma_f = 3e6 / 2.3548 -> sigma_t = 1 / (2 pi sigma_f) ~ 0.125 us
        Assert.Equal(1.249e-7, pulse.Sigma, 1e-9);
    }
}
