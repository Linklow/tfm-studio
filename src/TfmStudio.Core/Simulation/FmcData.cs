using TfmStudio.Core.Physics;

namespace TfmStudio.Core.Simulation;

/// <summary>
/// Full Matrix Capture: one A-scan for every transmitter/receiver pair of a linear array.
/// Samples are stored contiguously as <c>[tx][rx][sample]</c> (float32, arbitrary amplitude units).
/// </summary>
public sealed class FmcData
{
    public FmcData(LinearArray array, double samplingRate, int sampleCount, double timeOffset = 0, float[]? data = null)
    {
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        Array = array;
        SamplingRate = samplingRate;
        SampleCount = sampleCount;
        TimeOffset = timeOffset;
        int expected = array.ElementCount * array.ElementCount * sampleCount;
        if (data is not null && data.Length != expected)
            throw new ArgumentException($"expected {expected} samples, got {data.Length}", nameof(data));
        Data = data ?? new float[expected];
    }

    public LinearArray Array { get; }
    public int ElementCount => Array.ElementCount;
    public double SamplingRate { get; }
    public int SampleCount { get; }

    /// <summary>Time of sample 0 relative to firing, s.</summary>
    public double TimeOffset { get; }

    public float[] Data { get; }

    public int Offset(int tx, int rx) => (tx * ElementCount + rx) * SampleCount;

    public Span<float> AScan(int tx, int rx) => Data.AsSpan(Offset(tx, rx), SampleCount);

    public double TimeOfSample(double sample) => TimeOffset + sample / SamplingRate;
}
