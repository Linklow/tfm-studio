namespace TfmStudio.Core.Physics;

/// <summary>
/// A 1-D linear phased-array probe in contact with the specimen surface (z = 0).
/// Element <c>n</c> is centred at <see cref="ElementX"/>; the array centre is at <see cref="CenterX"/>.
/// </summary>
/// <param name="ElementCount">Number of elements.</param>
/// <param name="Pitch">Centre-to-centre element spacing, m.</param>
/// <param name="ElementWidth">Active element width in the array direction, m.</param>
/// <param name="CenterFrequency">Nominal centre frequency, Hz.</param>
/// <param name="FractionalBandwidth">-6 dB bandwidth divided by centre frequency.</param>
/// <param name="CenterX">Array centre x coordinate, m.</param>
public sealed record LinearArray(
    int ElementCount,
    double Pitch,
    double ElementWidth,
    double CenterFrequency,
    double FractionalBandwidth,
    double CenterX = 0)
{
    /// <summary>A common 5 MHz, 64-element, 0.6 mm pitch weld-inspection probe ("5L64").</summary>
    public static LinearArray Probe5L64 { get; } = new(64, 0.6e-3, 0.5e-3, 5e6, 0.6);

    /// <summary>A 5 MHz, 32-element, 0.6 mm pitch probe ("5L32").</summary>
    public static LinearArray Probe5L32 { get; } = new(32, 0.6e-3, 0.5e-3, 5e6, 0.6);

    public double ActiveAperture => (ElementCount - 1) * Pitch + ElementWidth;

    public double ElementX(int n) => CenterX + (n - (ElementCount - 1) / 2.0) * Pitch;

    public double[] ElementPositions()
    {
        var x = new double[ElementCount];
        for (int n = 0; n < ElementCount; n++) x[n] = ElementX(n);
        return x;
    }

    /// <summary>
    /// Far-field directivity of a single element radiating into a solid (2-D, rigid baffle):
    /// <c>D(θ) = sinc(π·w·sinθ/λ)·cosθ</c>.
    /// </summary>
    public double Directivity(double sinTheta, double cosTheta, double wavelength)
    {
        double u = Math.PI * ElementWidth * sinTheta / wavelength;
        double sinc = Math.Abs(u) < 1e-9 ? 1.0 : Math.Sin(u) / u;
        return sinc * cosTheta;
    }
}
