namespace TfmStudio.Core.Imaging;

/// <summary>Regular Cartesian image grid in the (x, z) plane; row-major, row = depth.</summary>
public sealed record ImageGrid(double XMin, double ZMin, double Dx, double Dz, int Width, int Height)
{
    public int PixelCount => Width * Height;
    public double XMax => XMin + (Width - 1) * Dx;
    public double ZMax => ZMin + (Height - 1) * Dz;

    public double X(int column) => XMin + column * Dx;
    public double Z(int row) => ZMin + row * Dz;

    public static ImageGrid FromBounds(double xMin, double xMax, double zMin, double zMax, double pixelSize)
    {
        int w = (int)Math.Round((xMax - xMin) / pixelSize) + 1;
        int h = (int)Math.Round((zMax - zMin) / pixelSize) + 1;
        return new ImageGrid(xMin, zMin, pixelSize, pixelSize, w, h);
    }

    /// <summary>Same grid shifted laterally by <paramref name="dx"/> (probe moved along x).</summary>
    public ImageGrid Shifted(double dx) => this with { XMin = XMin + dx };
}
