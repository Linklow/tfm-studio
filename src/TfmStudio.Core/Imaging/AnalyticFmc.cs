using System.Numerics;
using TfmStudio.Core.Dsp;
using TfmStudio.Core.Simulation;

namespace TfmStudio.Core.Imaging;

/// <summary>
/// FMC converted to analytic (I/Q) signals and laid out for the delay-and-sum kernels.
/// <para>
/// Samples are interleaved: <c>[re0, im0, re1, im1, ...]</c>. One 128-bit load at sample i then
/// returns everything linear interpolation needs (re_i, im_i, re_i+1, im_i+1).
/// Each A-scan occupies <see cref="SamplesPerSignal"/> = SampleCount + 3 complex samples: a
/// leading zero, the data, two trailing zeros. A kernel can clamp any delay to
/// [0, SampleCount + 1] and read samples i and i + 1 without bounds checks; out-of-range delays
/// read zeros.
/// </para>
/// <para>
/// In half-matrix (HMC) mode only pairs tx ≤ rx are kept, and each stored signal is
/// h(tx,rx) + h(rx,tx) (the diagonal is left unchanged). This halves the work whenever the
/// transmit and receive delay tables are equal (TFM, S-scan with transmit = receive focal law).
/// </para>
/// </summary>
public sealed class AnalyticFmc
{
    private AnalyticFmc(int elementCount, int sampleCount, double samplingRate, double timeOffset,
        int[] pairTx, int[] pairRx, bool halfMatrix)
    {
        ElementCount = elementCount;
        SampleCount = sampleCount;
        SamplingRate = samplingRate;
        TimeOffset = timeOffset;
        PairTx = pairTx;
        PairRx = pairRx;
        HalfMatrix = halfMatrix;
        IQ = new float[(long)pairTx.Length * Stride];
    }

    public int ElementCount { get; }
    public int SampleCount { get; }

    /// <summary>Complex samples per stored signal (data + 3 zero samples of padding).</summary>
    public int SamplesPerSignal => SampleCount + 3;

    /// <summary>Floats per stored signal (2 per complex sample).</summary>
    public int Stride => 2 * SamplesPerSignal;

    public double SamplingRate { get; }
    public double TimeOffset { get; }
    public bool HalfMatrix { get; }
    public int PairCount => PairTx.Length;
    public int[] PairTx { get; }
    public int[] PairRx { get; }

    /// <summary>Interleaved I/Q samples, <c>[pair][sample][re, im]</c>.</summary>
    public float[] IQ { get; }

    /// <summary>Largest valid (clamped) fractional sample index.</summary>
    public float MaxIndex => SampleCount + 1;

    /// <summary>Real part of stored sample <paramref name="i"/> (padded index) of pair <paramref name="k"/>.</summary>
    public float Re(int k, int i) => IQ[k * Stride + 2 * i];

    /// <summary>Imaginary part of stored sample <paramref name="i"/> (padded index) of pair <paramref name="k"/>.</summary>
    public float Im(int k, int i) => IQ[k * Stride + 2 * i + 1];

    public static AnalyticFmc From(FmcData fmc, bool halfMatrix)
    {
        int n = fmc.ElementCount;
        var tx = new List<int>();
        var rx = new List<int>();
        for (int t = 0; t < n; t++)
            for (int r = halfMatrix ? t : 0; r < n; r++) { tx.Add(t); rx.Add(r); }

        var a = new AnalyticFmc(n, fmc.SampleCount, fmc.SamplingRate, fmc.TimeOffset, tx.ToArray(), rx.ToArray(), halfMatrix);
        int ns = fmc.SampleCount;
        int wl = Hilbert.WorkspaceLength(ns);
        Parallel.For(0, a.PairCount,
            () => (Work: new Complex[wl], Sum: new float[ns], Re: new float[ns], Im: new float[ns]),
            (k, _, local) =>
            {
                int t = a.PairTx[k], r = a.PairRx[k];
                ReadOnlySpan<float> src;
                if (halfMatrix && t != r)
                {
                    var s1 = fmc.AScan(t, r);
                    var s2 = fmc.AScan(r, t);
                    for (int i = 0; i < ns; i++) local.Sum[i] = s1[i] + s2[i];
                    src = local.Sum;
                }
                else src = fmc.AScan(t, r);
                Hilbert.Analytic(src, local.Re, local.Im, local.Work);
                var dst = a.IQ.AsSpan(k * a.Stride + 2, 2 * ns); // skip padded sample 0
                for (int i = 0; i < ns; i++)
                {
                    dst[2 * i] = local.Re[i];
                    dst[2 * i + 1] = local.Im[i];
                }
                return local;
            },
            _ => { });
        return a;
    }
}
