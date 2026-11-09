using ScottPlot;
using TfmStudio.Core.Acquisition;
using TfmStudio.Core.Imaging;

namespace TfmStudio.Plotting;

/// <summary>Shared colours: a validated categorical order for series, neutral ink and chrome.</summary>
public static class Theme
{
    public static readonly Color Series1 = Color.FromHex("#2a78d6");
    public static readonly Color Series2 = Color.FromHex("#eb6834");
    public static readonly Color Series3 = Color.FromHex("#1baf7a");
    public static readonly Color Ink = Color.FromHex("#0b0b0b");
    public static readonly Color Ink2 = Color.FromHex("#52514e");
    public static readonly Color Muted = Color.FromHex("#898781");
    public static readonly Color Grid = Color.FromHex("#e1e0d9");
    public static readonly Color Surface = Color.FromHex("#fcfcfb");

    /// <summary>Applies the shared light theme to a plot.</summary>
    public static Plot Style(Plot plt)
    {
        plt.FigureBackground.Color = Surface;
        plt.DataBackground.Color = Surface;
        plt.Grid.MajorLineColor = Grid;
        plt.Axes.Color(Ink2);
        return plt;
    }
}

/// <summary>
/// Sequential single-hue ramp for amplitude images: near-surface for the bottom of the dynamic
/// range up to dark navy at 0 dB. Lightness is monotonic, so the image reads correctly in grey scale
/// and for colour-vision deficiencies.
/// </summary>
public sealed class AmplitudeColormap : IColormap
{
    private static readonly Color[] Stops =
    [
        Color.FromHex("#fcfcfb"), Color.FromHex("#cde2fb"), Color.FromHex("#86b6ef"),
        Color.FromHex("#3987e5"), Color.FromHex("#1c5cab"), Color.FromHex("#104281"), Color.FromHex("#0b1a33"),
    ];

    public string Name => "Amplitude (blue)";

    public Color GetColor(double normalizedIntensity)
    {
        if (double.IsNaN(normalizedIntensity)) return Colors.Transparent;
        double u = Math.Clamp(normalizedIntensity, 0, 1) * (Stops.Length - 1);
        int i = Math.Min((int)u, Stops.Length - 2);
        double f = u - i;
        Color a = Stops[i], b = Stops[i + 1];
        return new Color((byte)(a.R + f * (b.R - a.R)), (byte)(a.G + f * (b.G - a.G)), (byte)(a.B + f * (b.B - a.B)));
    }
}

/// <summary>Conversions from reconstructed images to ScottPlot heatmap data.</summary>
public static class Heatmaps
{
    private const double Mm = 1e-3;

    /// <summary>dB image clipped to [-range, 0]; NaN (outside the imaged area) is preserved.</summary>
    public static double[,] ToDb(ImageFrame img, double range, float? reference = null)
    {
        var g = img.Grid;
        float r = reference ?? img.Max();
        var data = new double[g.Height, g.Width];
        for (int row = 0; row < g.Height; row++)
            for (int c = 0; c < g.Width; c++)
            {
                float a = img[c, row];
                data[row, c] = float.IsNaN(a) ? double.NaN : Math.Max(20 * Math.Log10(Math.Max(a, 1e-12f) / r), -range);
            }
        return data;
    }

    public static double[,] ToDb(CScan cscan, double range)
    {
        float max = Math.Max(cscan.Values.Max(), 1e-12f);
        var data = new double[cscan.Height, cscan.Width];
        for (int r = 0; r < cscan.Height; r++)
            for (int c = 0; c < cscan.Width; c++)
            {
                float v = cscan.Values[r * cscan.Width + c];
                data[r, c] = v <= 0 ? double.NaN : Math.Max(20 * Math.Log10(v / max), -range);
            }
        return data;
    }

    /// <summary>
    /// Extent (mm) for a depth image. ScottPlot draws data row 0 at the extent's bottom edge; with
    /// the inverted depth axis that edge is the top of the screen.
    /// </summary>
    public static CoordinateRect DepthExtent(ImageGrid g) => new(
        (g.XMin - g.Dx / 2) / Mm, (g.XMax + g.Dx / 2) / Mm,
        (g.ZMin - g.Dz / 2) / Mm, (g.ZMax + g.Dz / 2) / Mm);

    public static CoordinateRect ScanExtent(CScan c) => new(
        (c.Grid.XMin - c.Grid.Dx / 2) / Mm, (c.Grid.XMax + c.Grid.Dx / 2) / Mm,
        (c.Plan.YStart - c.Plan.Step / 2) / Mm, (c.Plan.YEnd + c.Plan.Step / 2) / Mm);
}
