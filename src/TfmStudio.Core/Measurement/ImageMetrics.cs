using TfmStudio.Core.Imaging;

namespace TfmStudio.Core.Measurement;

/// <summary>An indication found in an image.</summary>
/// <param name="X">Peak position x, m.</param>
/// <param name="Z">Peak position z (depth), m.</param>
/// <param name="Amplitude">Peak amplitude.</param>
/// <param name="Length">Lateral extent of the -6 dB region, m.</param>
/// <param name="Height">Through-wall extent of the -6 dB region, m.</param>
/// <param name="Area">Area of the -6 dB region, m².</param>
public readonly record struct Indication(double X, double Z, double Amplitude, double Length, double Height, double Area);

/// <summary>Image-quality metrics and sizing used for TFM / PAUT comparisons.</summary>
public static class ImageMetrics
{
    /// <summary>Maximum inside a circle of <paramref name="radius"/> around (x, z).</summary>
    public static (int Column, int Row, float Amplitude) PeakNear(ImageFrame img, double x, double z, double radius)
    {
        var g = img.Grid;
        int best = -1;
        float max = float.NegativeInfinity;
        int c0 = Math.Max(0, (int)Math.Floor((x - radius - g.XMin) / g.Dx));
        int c1 = Math.Min(g.Width - 1, (int)Math.Ceiling((x + radius - g.XMin) / g.Dx));
        int r0 = Math.Max(0, (int)Math.Floor((z - radius - g.ZMin) / g.Dz));
        int r1 = Math.Min(g.Height - 1, (int)Math.Ceiling((z + radius - g.ZMin) / g.Dz));
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                double dx = g.X(c) - x, dz = g.Z(r) - z;
                if (dx * dx + dz * dz > radius * radius) continue;
                float v = img[c, r];
                if (v > max) { max = v; best = r * g.Width + c; }
            }
        if (best < 0) throw new ArgumentException("search region is outside the image");
        return (best % g.Width, best / g.Width, max);
    }

    /// <summary>
    /// -6 dB sizing: the connected region around the peak near (x, z) where the amplitude is at
    /// least half the peak. Returns its extents and area.
    /// </summary>
    public static Indication SizeAt(ImageFrame img, double x, double z, double searchRadius, double dropDb = 6)
    {
        var g = img.Grid;
        var (pc, pr, peak) = PeakNear(img, x, z, searchRadius);
        float level = peak * MathF.Pow(10, (float)(-dropDb / 20));
        var visited = new bool[g.PixelCount];
        var stack = new Stack<int>();
        stack.Push(pr * g.Width + pc);
        visited[pr * g.Width + pc] = true;
        int minC = pc, maxC = pc, minR = pr, maxR = pr, count = 0;
        while (stack.Count > 0)
        {
            int p = stack.Pop();
            int c = p % g.Width, r = p / g.Width;
            count++;
            minC = Math.Min(minC, c); maxC = Math.Max(maxC, c);
            minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
            Span<(int, int)> nb = [(c - 1, r), (c + 1, r), (c, r - 1), (c, r + 1)];
            foreach (var (nc, nr) in nb)
            {
                if (nc < 0 || nr < 0 || nc >= g.Width || nr >= g.Height) continue;
                int q = nr * g.Width + nc;
                if (visited[q] || !(img.Amplitude[q] >= level)) continue;
                visited[q] = true;
                stack.Push(q);
            }
        }
        return new Indication(g.X(pc), g.Z(pr), peak, (maxC - minC + 1) * g.Dx, (maxR - minR + 1) * g.Dz,
            count * g.Dx * g.Dz);
    }

    /// <summary>
    /// Array Performance Indicator (Holmes, Drinkwater and Wilcox, 2005): area of the -6 dB region of a
    /// point-like indication divided by λ². Smaller is sharper.
    /// </summary>
    public static double ArrayPerformanceIndicator(ImageFrame img, double x, double z, double wavelength,
        double searchRadius = 2e-3) => SizeAt(img, x, z, searchRadius).Area / (wavelength * wavelength);

    /// <summary>
    /// Two-point resolution: depth of the amplitude dip on the straight line between two reflectors,
    /// in dB relative to the weaker peak. Values below -6 dB mean the pair is resolved.
    /// </summary>
    public static double DipBetween(ImageFrame img, (double X, double Z) a, (double X, double Z) b, double searchRadius = 0.6e-3)
    {
        var (ac, ar, pa) = PeakNear(img, a.X, a.Z, searchRadius);
        var (bc, br, pb) = PeakNear(img, b.X, b.Z, searchRadius);
        var g = img.Grid;
        int steps = 200;
        float min = float.PositiveInfinity;
        for (int s = 1; s < steps; s++)
        {
            double t = (double)s / steps;
            double x = g.X(ac) + t * (g.X(bc) - g.X(ac));
            double z = g.Z(ar) + t * (g.Z(br) - g.Z(ar));
            float v = img.Sample(x, z);
            if (v < min) min = v;
        }
        return 20 * Math.Log10(min / Math.Min(pa, pb));
    }

    /// <summary>Signal-to-noise ratio: peak near (x, z) over the RMS of a noise region, dB.</summary>
    public static double SignalToNoise(ImageFrame img, double x, double z, (double X0, double X1, double Z0, double Z1) noiseRegion)
    {
        var (_, _, peak) = PeakNear(img, x, z, 1.5e-3);
        var g = img.Grid;
        double sum = 0;
        int n = 0;
        for (int r = 0; r < g.Height; r++)
        {
            double zz = g.Z(r);
            if (zz < noiseRegion.Z0 || zz > noiseRegion.Z1) continue;
            for (int c = 0; c < g.Width; c++)
            {
                double xx = g.X(c);
                if (xx < noiseRegion.X0 || xx > noiseRegion.X1) continue;
                float v = img[c, r];
                if (float.IsNaN(v)) continue;
                sum += v * v;
                n++;
            }
        }
        return 20 * Math.Log10(peak / Math.Sqrt(sum / Math.Max(n, 1)));
    }
}
