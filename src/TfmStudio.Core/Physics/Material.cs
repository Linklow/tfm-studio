namespace TfmStudio.Core.Physics;

/// <summary>Isotropic test material. SI units: m/s, kg/m³.</summary>
/// <param name="Name">Display name.</param>
/// <param name="LongitudinalVelocity">Bulk longitudinal (compression) wave velocity, m/s.</param>
/// <param name="ShearVelocity">Bulk shear wave velocity, m/s.</param>
/// <param name="Density">Density, kg/m³.</param>
/// <param name="AttenuationDbPerMmPerMHz">Amplitude attenuation of longitudinal waves, dB/(mm·MHz).</param>
public sealed record Material(
    string Name,
    double LongitudinalVelocity,
    double ShearVelocity,
    double Density,
    double AttenuationDbPerMmPerMHz)
{
    public static Material Steel { get; } = new("Carbon steel", 5920, 3230, 7850, 0.004);
    public static Material StainlessSteel { get; } = new("Stainless steel 304", 5790, 3100, 7900, 0.010);
    public static Material Aluminium { get; } = new("Aluminium", 6320, 3130, 2700, 0.002);

    /// <summary>Amplitude attenuation coefficient at <paramref name="frequency"/>, nepers per metre.</summary>
    public double AttenuationNeperPerMetre(double frequency) =>
        AttenuationDbPerMmPerMHz * (frequency / 1e6) * 1000.0 * Math.Log(10) / 20.0;

    /// <summary>Longitudinal wavelength at <paramref name="frequency"/>, m.</summary>
    public double Wavelength(double frequency) => LongitudinalVelocity / frequency;
}
