namespace TfmStudio.Core.Imaging;

/// <summary>A reconstructed amplitude image (linear units). NaN marks pixels outside the imaged area.</summary>
public sealed class ImageFrame
{
    public ImageFrame(ImageGrid grid, float[] amplitude, string method)
    {
        if (amplitude.Length != grid.PixelCount) throw new ArgumentException("size mismatch", nameof(amplitude));
        Grid = grid;
        Amplitude = amplitude;
        Method = method;
    }

    public ImageGrid Grid { get; }
    public float[] Amplitude { get; }
    public string Method { get; }

    /// <summary>Time spent reconstructing the image.</summary>
    public TimeSpan ComputeTime { get; init; }

    public float this[int column, int row] => Amplitude[row * Grid.Width + column];

    public float Max()
    {
        float m = 0;
        foreach (float v in Amplitude) if (v > m) m = v; // NaN comparisons are false
        return m;
    }

    /// <summary>Bilinear sample at (x, z); NaN outside the grid.</summary>
    public float Sample(double x, double z)
    {
        double u = (x - Grid.XMin) / Grid.Dx, v = (z - Grid.ZMin) / Grid.Dz;
        if (u < 0 || v < 0 || u > Grid.Width - 1 || v > Grid.Height - 1) return float.NaN;
        int i = Math.Min((int)u, Grid.Width - 2), j = Math.Min((int)v, Grid.Height - 2);
        float fu = (float)(u - i), fv = (float)(v - j);
        float a = this[i, j], b = this[i + 1, j], c = this[i, j + 1], d = this[i + 1, j + 1];
        return (a * (1 - fu) + b * fu) * (1 - fv) + (c * (1 - fu) + d * fu) * fv;
    }

    /// <summary>Image in dB relative to <paramref name="reference"/> (default: image maximum).</summary>
    public float[] ToDecibels(float? reference = null)
    {
        float r = reference ?? Max();
        var db = new float[Amplitude.Length];
        for (int i = 0; i < db.Length; i++)
            db[i] = float.IsNaN(Amplitude[i]) ? float.NaN : 20f * MathF.Log10(MathF.Max(Amplitude[i], 1e-12f) / r);
        return db;
    }
}
