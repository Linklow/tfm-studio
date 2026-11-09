namespace TfmStudio.Core.Physics;

/// <summary>
/// A reflector in the specimen. Coordinates: x along the array, z depth below the scanning
/// surface, y along the scan (encoder) axis. A reflector exists for <c>YStart ≤ y ≤ YEnd</c>.
/// </summary>
public abstract record Reflector(string Label, double YStart, double YEnd)
{
    public bool IsPresentAt(double y) => y >= YStart && y <= YEnd;
}

/// <summary>Side-drilled hole (the standard reference reflector), axis parallel to y.</summary>
/// <param name="X">Hole centre x, m.</param>
/// <param name="Z">Hole centre depth, m.</param>
/// <param name="Diameter">Hole diameter, m.</param>
public sealed record SideDrilledHole(string Label, double X, double Z, double Diameter,
    double YStart = double.NegativeInfinity, double YEnd = double.PositiveInfinity)
    : Reflector(Label, YStart, YEnd);

/// <summary>Small volumetric flaw (pore, inclusion) treated as a point scatterer.</summary>
/// <param name="Reflectivity">Relative scattering amplitude (a 2 mm SDH is about 0.25).</param>
public sealed record PointFlaw(string Label, double X, double Z, double Reflectivity,
    double YStart = double.NegativeInfinity, double YEnd = double.PositiveInfinity)
    : Reflector(Label, YStart, YEnd);

/// <summary>
/// Back-wall-breaking planar flaw (fatigue crack or EDM notch), vertical, rising from the back
/// wall to height <paramref name="Height"/>. Modelled as a strong corner-trap reflector at the
/// base plus a weak diffracting tip — the two echoes used for crack-height sizing.
/// </summary>
public sealed record BackWallCrack(string Label, double X, double Height,
    double YStart = double.NegativeInfinity, double YEnd = double.PositiveInfinity)
    : Reflector(Label, YStart, YEnd);
