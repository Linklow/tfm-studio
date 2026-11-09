using TfmStudio.Core.Acquisition;
using TfmStudio.Core.Imaging;
using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;

namespace TfmStudio.Core;

/// <summary>Ready-made inspection scenarios.</summary>
public static class Scenarios
{
    private const double Mm = 1e-3;

    /// <summary>
    /// A 30 mm carbon-steel test plate scanned along y with a 5L64 probe. Flaws along the scan:
    /// a reference Ø2 mm SDH running the full length, porosity, a slag-like inclusion, a 6 mm
    /// back-wall-breaking root crack and a pair of Ø1 mm SDHs 1.5 mm apart (resolution target).
    /// </summary>
    public static Specimen TestPlate() => new(
        Material.Steel,
        Thickness: 30 * Mm,
        Reflectors:
        [
            new SideDrilledHole("Reference SDH Ø2", -8 * Mm, 12 * Mm, 2 * Mm, 5 * Mm, 195 * Mm),
            new PointFlaw("Pore 1", 6.0 * Mm, 14.0 * Mm, 0.07, 30 * Mm, 46 * Mm),
            new PointFlaw("Pore 2", 7.2 * Mm, 15.1 * Mm, 0.06, 33 * Mm, 44 * Mm),
            new PointFlaw("Pore 3", 5.1 * Mm, 15.6 * Mm, 0.05, 36 * Mm, 48 * Mm),
            new PointFlaw("Pore 4", 6.6 * Mm, 13.0 * Mm, 0.05, 38 * Mm, 45 * Mm),
            new PointFlaw("Inclusion", -2 * Mm, 6 * Mm, 0.12, 60 * Mm, 74 * Mm),
            new BackWallCrack("Root crack h=6", 4 * Mm, 6 * Mm, 90 * Mm, 128 * Mm),
            new SideDrilledHole("Pair SDH A", -5.0 * Mm, 22 * Mm, 1 * Mm, 145 * Mm, 175 * Mm),
            new SideDrilledHole("Pair SDH B", -3.5 * Mm, 22 * Mm, 1 * Mm, 145 * Mm, 175 * Mm),
        ],
        GrainDensity: 0.02,
        GrainReflectivity: 0.012,
        XMin: -70 * Mm, XMax: 70 * Mm, YMin: 0, YMax: 200 * Mm);

    public static ScanPlan TestPlateScan(double speed = 0) => new(0, 200 * Mm, 1 * Mm, speed);

    /// <summary>Image grid relative to the probe centre: ±22 mm, 1-33 mm deep, 0.2 mm pixels.</summary>
    public static ImageGrid DefaultGrid(double pixel = 0.2 * Mm) =>
        ImageGrid.FromBounds(-22 * Mm, 22 * Mm, 1 * Mm, 33 * Mm, pixel);

    public static SimulationSettings DefaultSimulation { get; } = new();
}
