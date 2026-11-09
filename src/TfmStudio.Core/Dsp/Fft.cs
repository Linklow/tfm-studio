using System.Numerics;

namespace TfmStudio.Core.Dsp;

/// <summary>
/// Iterative radix-2 Cooley-Tukey FFT for power-of-two lengths. A plan is immutable (precomputed
/// twiddles and bit-reversal permutation) and can be shared between threads.
/// </summary>
public sealed class FftPlan
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, FftPlan> Cache = new();
    private readonly Complex[] _twiddles;
    private readonly int[] _bitReverse;

    private FftPlan(int length)
    {
        if (length < 2 || (length & (length - 1)) != 0)
            throw new ArgumentException("FFT length must be a power of two >= 2", nameof(length));
        Length = length;
        _twiddles = new Complex[length / 2];
        for (int k = 0; k < length / 2; k++)
            _twiddles[k] = Complex.FromPolarCoordinates(1, -2 * Math.PI * k / length);
        int bits = System.Numerics.BitOperations.Log2((uint)length);
        _bitReverse = new int[length];
        for (int i = 0; i < length; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _bitReverse[i] = r;
        }
    }

    public int Length { get; }

    public static FftPlan Get(int length) => Cache.GetOrAdd(length, l => new FftPlan(l));

    public static int NextPowerOfTwo(int n) => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(n, 2));

    /// <summary>In-place forward transform, X[k] = Σ x[n]·exp(-2πi·kn/N).</summary>
    public void Forward(Span<Complex> x) => Transform(x, inverse: false);

    /// <summary>In-place inverse transform including the 1/N scale.</summary>
    public void Inverse(Span<Complex> x)
    {
        Transform(x, inverse: true);
        double s = 1.0 / Length;
        for (int i = 0; i < x.Length; i++) x[i] *= s;
    }

    private void Transform(Span<Complex> x, bool inverse)
    {
        if (x.Length != Length) throw new ArgumentException("length mismatch", nameof(x));
        for (int i = 0; i < Length; i++)
        {
            int j = _bitReverse[i];
            if (j > i) (x[i], x[j]) = (x[j], x[i]);
        }
        for (int size = 2; size <= Length; size <<= 1)
        {
            int half = size >> 1, step = Length / size;
            for (int start = 0; start < Length; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    Complex w = _twiddles[k * step];
                    if (inverse) w = Complex.Conjugate(w);
                    Complex a = x[start + k];
                    Complex b = x[start + k + half] * w;
                    x[start + k] = a + b;
                    x[start + k + half] = a - b;
                }
            }
        }
    }
}
