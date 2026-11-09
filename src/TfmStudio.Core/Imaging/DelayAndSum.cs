using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using X86Avx2 = System.Runtime.Intrinsics.X86.Avx2;

namespace TfmStudio.Core.Imaging;

/// <summary>Implementation of the delay-and-sum inner loop.</summary>
public enum DasKernel
{
    /// <summary>Plain loops, single thread. Reference implementation.</summary>
    Scalar,
    /// <summary>Plain loops, image rows in parallel.</summary>
    Parallel,
    /// <summary>AVX2 gathers (4 per 8 pixels) + FMA, rows in parallel. Kept for comparison: gathers are slow on AMD Zen.</summary>
    Avx2Gather,
    /// <summary>AVX2 index math + one 128-bit I/Q load per pixel + FMA, rows in parallel.</summary>
    Avx2Interleaved,
    /// <summary>Fastest kernel available on this CPU.</summary>
    Auto,
}

/// <summary>
/// Delay-and-sum beamforming over an FMC: for every pixel p
/// <code>I(p) = | Σ_pairs h_tx,rx( T[tx][p] + R[rx][p] + offset ) |</code>
/// where h is the analytic signal (linearly interpolated) and T/R are transmit/receive delay tables
/// in samples. TFM uses the same one-way table for T and R. A phased-array S-scan uses a focal-law
/// table for T. Because one kernel serves both, TFM and PAUT images are reconstructed
/// identically apart from the delays.
/// </summary>
public static class DelayAndSum
{
    public static bool SimdSupported => X86Avx2.IsSupported && Fma.IsSupported;

    public static DasKernel Resolve(DasKernel kernel) => kernel switch
    {
        DasKernel.Auto => SimdSupported ? DasKernel.Avx2Interleaved : DasKernel.Parallel,
        DasKernel.Avx2Gather or DasKernel.Avx2Interleaved when !SimdSupported => DasKernel.Parallel,
        _ => kernel,
    };

    /// <summary>Beamforms into <paramref name="image"/> (amplitude of the coherent sum).</summary>
    /// <param name="signals">Analytic FMC (full or half matrix).</param>
    /// <param name="tx">Transmit delay table, samples.</param>
    /// <param name="rx">Receive delay table, samples (may be the same instance as <paramref name="tx"/>).</param>
    /// <param name="image">Output, one value per pixel.</param>
    /// <param name="kernel">Inner-loop implementation.</param>
    /// <returns>Wall-clock time of the reconstruction.</returns>
    public static TimeSpan Run(AnalyticFmc signals, DelayTable tx, DelayTable rx, float[] image, DasKernel kernel = DasKernel.Auto)
    {
        if (tx.Grid.PixelCount != image.Length || rx.Grid.PixelCount != image.Length)
            throw new ArgumentException("delay tables and image must share the grid");
        if (tx.ElementCount != signals.ElementCount || rx.ElementCount != signals.ElementCount)
            throw new ArgumentException("delay tables and signals must have the same element count");
        // index = delay_tx + delay_rx - t0*fs + 1 (the +1 skips the leading zero sample)
        float offset = (float)(1 - signals.TimeOffset * signals.SamplingRate);
        var sw = Stopwatch.StartNew();
        int width = tx.Grid.Width, height = tx.Grid.Height;
        switch (Resolve(kernel))
        {
            case DasKernel.Scalar:
                for (int row = 0; row < height; row++) RowScalar(signals, tx, rx, image, offset, row * width, width);
                break;
            case DasKernel.Parallel:
                System.Threading.Tasks.Parallel.For(0, height, row => RowScalar(signals, tx, rx, image, offset, row * width, width));
                break;
            case DasKernel.Avx2Gather:
                System.Threading.Tasks.Parallel.For(0, height, row => RowGather(signals, tx, rx, image, offset, row * width, width));
                break;
            default:
                System.Threading.Tasks.Parallel.For(0, height, row => RowInterleaved(signals, tx, rx, image, offset, row * width, width));
                break;
        }
        return sw.Elapsed;
    }

    /// <summary>Reference kernel: readable, bounds-checked, no intrinsics.</summary>
    private static void RowScalar(AnalyticFmc a, DelayTable tx, DelayTable rx, float[] image, float offset, int p0, int count)
    {
        int pixels = tx.Grid.PixelCount, stride = a.Stride;
        float maxIdx = a.MaxIndex;
        float[] iq = a.IQ, td = tx.Samples, rd = rx.Samples;
        var accRe = new float[count];
        var accIm = new float[count];
        for (int k = 0; k < a.PairCount; k++)
        {
            int tBase = a.PairTx[k] * pixels + p0, rBase = a.PairRx[k] * pixels + p0, sBase = k * stride;
            for (int x = 0; x < count; x++)
            {
                float idx = Math.Clamp(td[tBase + x] + rd[rBase + x] + offset, 0f, maxIdx);
                int i = (int)idx;
                float f = idx - i;
                int s = sBase + 2 * i;
                accRe[x] += iq[s] + f * (iq[s + 2] - iq[s]);
                accIm[x] += iq[s + 1] + f * (iq[s + 3] - iq[s + 1]);
            }
        }
        for (int x = 0; x < count; x++) image[p0 + x] = MathF.Sqrt(accRe[x] * accRe[x] + accIm[x] * accIm[x]);
    }

    /// <summary>
    /// 8 pixels per step with register accumulators. Indices and fractions are computed with AVX2;
    /// each pixel then needs one unaligned 128-bit load (re_i, im_i, re_i+1, im_i+1). Two pixels share
    /// one 128-bit accumulator, so the hot loop has no gathers and no memory-resident accumulators.
    /// </summary>
    private static unsafe void RowInterleaved(AnalyticFmc a, DelayTable tx, DelayTable rx, float[] image, float offset, int p0, int count)
    {
        int pixels = tx.Grid.PixelCount, stride = a.Stride, pairs = a.PairCount;
        int[] pairTx = a.PairTx, pairRx = a.PairRx;
        var vOffset = Vector256.Create(offset);
        var vMax = Vector256.Create(a.MaxIndex);
        int* idx = stackalloc int[8];
        int x = 0;
        fixed (float* iq = a.IQ, td = tx.Samples, rd = rx.Samples, img = image)
        {
            for (; x <= count - 8; x += 8)
            {
                int p = p0 + x;
                Vector128<float> acc01 = default, acc23 = default, acc45 = default, acc67 = default;
                for (int k = 0; k < pairs; k++)
                {
                    var d = Avx.Add(Avx.Add(Avx.LoadVector256(td + pairTx[k] * pixels + p),
                                            Avx.LoadVector256(rd + pairRx[k] * pixels + p)), vOffset);
                    d = Avx.Min(Avx.Max(d, Vector256<float>.Zero), vMax);
                    var vi = Avx.ConvertToVector256Int32WithTruncation(d);
                    var f = Avx.Subtract(d, Avx.ConvertToVector256Single(vi));
                    Avx.Store(idx, vi);
                    float* s = iq + (long)k * stride;
                    var fLo = f.GetLower();
                    var fHi = f.GetUpper();
                    acc01 = Sse.Add(acc01, Lerp2(s, idx[0], idx[1], Sse.UnpackLow(fLo, fLo)));
                    acc23 = Sse.Add(acc23, Lerp2(s, idx[2], idx[3], Sse.UnpackHigh(fLo, fLo)));
                    acc45 = Sse.Add(acc45, Lerp2(s, idx[4], idx[5], Sse.UnpackLow(fHi, fHi)));
                    acc67 = Sse.Add(acc67, Lerp2(s, idx[6], idx[7], Sse.UnpackHigh(fHi, fHi)));
                }
                // |z| for 8 pixels: square, add re²+im² pairwise, square root.
                var m0123 = Sse.Sqrt(Sse3.HorizontalAdd(Sse.Multiply(acc01, acc01), Sse.Multiply(acc23, acc23)));
                var m4567 = Sse.Sqrt(Sse3.HorizontalAdd(Sse.Multiply(acc45, acc45), Sse.Multiply(acc67, acc67)));
                Sse.Store(img + p, m0123);
                Sse.Store(img + p + 4, m4567);
            }
        }
        if (x < count) RowScalar(a, tx, rx, image, offset, p0 + x, count - x);
    }

    /// <summary>Interpolated I/Q of two pixels: returns (re_a, im_a, re_b, im_b).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector128<float> Lerp2(float* s, int ia, int ib, Vector128<float> f)
    {
        var va = Sse.LoadVector128(s + 2 * ia); // re_i, im_i, re_i+1, im_i+1 of pixel a
        var vb = Sse.LoadVector128(s + 2 * ib);
        var lo = Sse.MoveLowToHigh(va, vb);     // re_i a, im_i a, re_i b, im_i b
        var hi = Sse.MoveHighToLow(vb, va);     // re_i+1 a, im_i+1 a, re_i+1 b, im_i+1 b
        return Fma.MultiplyAdd(f, Sse.Subtract(hi, lo), lo);
    }

    /// <summary>AVX2 gather variant: 4 gathers of 8 lanes per step.</summary>
    private static unsafe void RowGather(AnalyticFmc a, DelayTable tx, DelayTable rx, float[] image, float offset, int p0, int count)
    {
        int pixels = tx.Grid.PixelCount, stride = a.Stride, pairs = a.PairCount;
        int[] pairTx = a.PairTx, pairRx = a.PairRx;
        var vOffset = Vector256.Create(offset);
        var vMax = Vector256.Create(a.MaxIndex);
        int x = 0;
        fixed (float* iq = a.IQ, td = tx.Samples, rd = rx.Samples, img = image)
        {
            for (; x <= count - 8; x += 8)
            {
                var accRe = Vector256<float>.Zero;
                var accIm = Vector256<float>.Zero;
                int p = p0 + x;
                for (int k = 0; k < pairs; k++)
                {
                    var d = Avx.Add(Avx.Add(Avx.LoadVector256(td + pairTx[k] * pixels + p),
                                            Avx.LoadVector256(rd + pairRx[k] * pixels + p)), vOffset);
                    d = Avx.Min(Avx.Max(d, Vector256<float>.Zero), vMax);
                    var i = Avx.ConvertToVector256Int32WithTruncation(d);
                    var f = Avx.Subtract(d, Avx.ConvertToVector256Single(i));
                    var i2 = X86Avx2.ShiftLeftLogical(i, 1);
                    float* s = iq + (long)k * stride;
                    var r0 = X86Avx2.GatherVector256(s, i2, 4);
                    var m0 = X86Avx2.GatherVector256(s + 1, i2, 4);
                    var r1 = X86Avx2.GatherVector256(s + 2, i2, 4);
                    var m1 = X86Avx2.GatherVector256(s + 3, i2, 4);
                    accRe = Avx.Add(accRe, Fma.MultiplyAdd(f, Avx.Subtract(r1, r0), r0));
                    accIm = Avx.Add(accIm, Fma.MultiplyAdd(f, Avx.Subtract(m1, m0), m0));
                }
                Avx.Store(img + p, Avx.Sqrt(Fma.MultiplyAdd(accRe, accRe, Avx.Multiply(accIm, accIm))));
            }
        }
        if (x < count) RowScalar(a, tx, rx, image, offset, p0 + x, count - x);
    }
}
