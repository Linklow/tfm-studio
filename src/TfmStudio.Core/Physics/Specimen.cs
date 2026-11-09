namespace TfmStudio.Core.Physics;

/// <summary>
/// A plate-like test piece scanned from its top surface (z = 0) with a back wall at z = Thickness.
/// </summary>
/// <param name="Material">Material.</param>
/// <param name="Thickness">Wall thickness, m.</param>
/// <param name="Reflectors">Flaws and reference reflectors.</param>
/// <param name="GrainDensity">Random grain scatterers per mm³ (microstructural noise).</param>
/// <param name="GrainReflectivity">RMS reflectivity of one grain scatterer.</param>
/// <param name="XMin">Lower x bound of the region populated with grain scatterers, m.</param>
/// <param name="XMax">Upper x bound, m.</param>
/// <param name="YMin">Lower y (scan axis) bound, m.</param>
/// <param name="YMax">Upper y bound, m.</param>
public sealed record Specimen(
    Material Material,
    double Thickness,
    IReadOnlyList<Reflector> Reflectors,
    double GrainDensity = 0.0,
    double GrainReflectivity = 0.0,
    double XMin = -0.06,
    double XMax = 0.06,
    double YMin = 0.0,
    double YMax = 0.2)
{
    public bool HasBackWall { get; init; } = true;
}
