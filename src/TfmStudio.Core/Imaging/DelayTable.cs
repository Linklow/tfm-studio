using TfmStudio.Core.Physics;

namespace TfmStudio.Core.Imaging;

/// <summary>
/// Per-element, per-pixel travel times in samples, laid out <c>[element][pixel]</c> so that a kernel
/// can load consecutive pixels of one element with a single vector load.
/// </summary>
public sealed class DelayTable
{
    public DelayTable(int elementCount, ImageGrid grid)
    {
        ElementCount = elementCount;
        Grid = grid;
        Samples = new float[elementCount * grid.PixelCount];
    }

    public int ElementCount { get; }
    public ImageGrid Grid { get; }
    public float[] Samples { get; }

    public Span<float> Row(int element) => Samples.AsSpan(element * Grid.PixelCount, Grid.PixelCount);

    /// <summary>One-way direct-contact travel time from each element to each pixel (TFM, receive DDF).</summary>
    public static DelayTable OneWay(LinearArray array, ImageGrid grid, double velocity, double samplingRate)
    {
        var t = new DelayTable(array.ElementCount, grid);
        double k = samplingRate / velocity;
        Parallel.For(0, array.ElementCount, e =>
        {
            double ex = array.ElementX(e);
            var row = t.Row(e);
            for (int j = 0; j < grid.Height; j++)
            {
                double z = grid.Z(j);
                for (int i = 0; i < grid.Width; i++)
                {
                    double dx = grid.X(i) - ex;
                    row[j * grid.Width + i] = (float)(Math.Sqrt(dx * dx + z * z) * k);
                }
            }
        });
        return t;
    }

    /// <summary>A delay that the kernels clamp to the zero-padded region: the pixel contributes nothing.</summary>
    public const float Masked = -1e9f;
}
