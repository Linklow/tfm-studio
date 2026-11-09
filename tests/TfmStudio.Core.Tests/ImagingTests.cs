using TfmStudio.Core.Dsp;
using TfmStudio.Core.Imaging;
using TfmStudio.Core.Measurement;
using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;

namespace TfmStudio.Core.Tests;

public class ImagingTests
{
    private const double Mm = 1e-3;
    private static readonly LinearArray Probe = LinearArray.Probe5L32;
    private static readonly double C = Material.Steel.LongitudinalVelocity;

    private static Specimen SinglePoint(double x, double z, bool backWall = false) =>
        new(Material.Steel, 30 * Mm, [new PointFlaw("p", x, z, 0.3)]) { HasBackWall = backWall };

    private static FmcData Simulate(Specimen s, double noise = 0) =>
        new FmcSimulator(s, new SimulationSettings { NoiseRms = noise }).Acquire(Probe);

    [Fact]
    public void Simulated_fmc_is_reciprocal_without_noise()
    {
        var fmc = Simulate(SinglePoint(3 * Mm, 12 * Mm, backWall: true));
        for (int t = 0; t < Probe.ElementCount; t += 5)
            for (int r = 0; r < Probe.ElementCount; r += 7)
                Assert.True(fmc.AScan(t, r).SequenceEqual(fmc.AScan(r, t)));
    }

    [Fact]
    public void Pulse_echo_times_match_geometry()
    {
        var specimen = new Specimen(Material.Steel, 25 * Mm, [new SideDrilledHole("sdh", 0, 10 * Mm, 2 * Mm)]);
        var fmc = new FmcSimulator(specimen, new SimulationSettings { NoiseRms = 0 }).Acquire(Probe);
        int e = Probe.ElementCount / 2; // x = +0.3 mm, nearly above the hole
        var env = Hilbert.Envelope(fmc.AScan(e, e));
        var hole = new Gate(2e-6, 3e-6, 0).Evaluate(env, fmc.SamplingRate);
        var wall = new Gate(7e-6, 2e-6, 0).Evaluate(env, fmc.SamplingRate);
        double rTop = Math.Sqrt(Math.Pow(Probe.ElementX(e), 2) + Math.Pow(10 * Mm, 2)) - 1 * Mm;
        Assert.Equal(2 * rTop / C, hole.Time, 0.03e-6);
        Assert.Equal(2 * 25 * Mm / C, wall.Time, 0.03e-6);
        Assert.True(wall.Amplitude > hole.Amplitude);
    }

    [Theory]
    [InlineData(0.0, 8.0)]
    [InlineData(-6.0, 15.0)]
    [InlineData(7.5, 22.0)]
    public void Tfm_places_a_point_flaw_at_its_true_position(double xMm, double zMm)
    {
        var fmc = Simulate(SinglePoint(xMm * Mm, zMm * Mm));
        var grid = ImageGrid.FromBounds(-15 * Mm, 15 * Mm, 2 * Mm, 28 * Mm, 0.1 * Mm);
        var img = new TfmImager(C).Reconstruct(fmc, grid);
        var (col, row, _) = ImageMetrics.PeakNear(img, xMm * Mm, zMm * Mm, 3 * Mm);
        Assert.Equal(xMm * Mm, grid.X(col), 0.15 * Mm);
        Assert.Equal(zMm * Mm, grid.Z(row), 0.15 * Mm);
    }

    [Fact]
    public void All_kernels_and_half_matrix_agree()
    {
        var fmc = new FmcSimulator(SinglePoint(2 * Mm, 14 * Mm, backWall: true), new SimulationSettings { NoiseRms = 0.01 }).Acquire(Probe);
        // Odd width exercises the scalar tail of the SIMD kernels.
        var grid = ImageGrid.FromBounds(-6 * Mm, 6.1 * Mm, 8 * Mm, 20 * Mm, 0.3 * Mm);
        var table = DelayTable.OneWay(Probe, grid, C, fmc.SamplingRate);
        var full = AnalyticFmc.From(fmc, halfMatrix: false);
        var half = AnalyticFmc.From(fmc, halfMatrix: true);
        var reference = new float[grid.PixelCount];
        DelayAndSum.Run(full, table, table, reference, DasKernel.Scalar);
        float max = reference.Max();
        var candidates = new List<(AnalyticFmc, DasKernel)> { (full, DasKernel.Parallel), (half, DasKernel.Scalar), (half, DasKernel.Auto) };
        if (DelayAndSum.SimdSupported)
            candidates.AddRange([(full, DasKernel.Avx2Gather), (full, DasKernel.Avx2Interleaved), (half, DasKernel.Avx2Interleaved)]);
        foreach (var (signals, kernel) in candidates)
        {
            var img = new float[grid.PixelCount];
            DelayAndSum.Run(signals, table, table, img, kernel);
            for (int p = 0; p < img.Length; p++)
                Assert.True(Math.Abs(img[p] - reference[p]) <= 2e-4 * max, $"{kernel} half={signals.HalfMatrix} pixel {p}: {img[p]} vs {reference[p]}");
        }
    }

    [Fact]
    public void Focal_law_aligns_all_wavefronts_at_the_focus()
    {
        var law = FocalLaws.Compute(Probe, C, 20 * Math.PI / 180, 15 * Mm);
        double fx = 15 * Mm * Math.Sin(law.Angle), fz = 15 * Mm * Math.Cos(law.Angle);
        for (int e = 0; e < Probe.ElementCount; e++)
        {
            double path = Math.Sqrt(Math.Pow(fx - Probe.ElementX(e), 2) + fz * fz);
            Assert.Equal(law.MaxPath / C, law.Delays[e] + path / C, 1e-12);
        }
        Assert.Equal(0, law.Delays.Min(), 1e-15);
    }

    [Fact]
    public void Steering_law_is_linear_in_element_position()
    {
        double angle = -30 * Math.PI / 180;
        var law = FocalLaws.Compute(Probe, C, angle, double.PositiveInfinity);
        double expectedStep = -Probe.Pitch * Math.Sin(angle) / C; // negative angle: last element fires first
        for (int e = 1; e < Probe.ElementCount; e++)
            Assert.Equal(-expectedStep, law.Delays[e] - law.Delays[e - 1], 1e-12);
    }

    [Fact]
    public void Sector_scan_locates_flaw_at_focus_and_masks_outside_sector()
    {
        var fmc = Simulate(SinglePoint(5 * Mm, 15 * Mm));
        var grid = ImageGrid.FromBounds(-25 * Mm, 25 * Mm, 1 * Mm, 25 * Mm, 0.2 * Mm);
        var imager = new SectorScanImager(C, -40, 40, 1, focalRange: Math.Sqrt(25 + 225) * Mm);
        var img = imager.Reconstruct(fmc, grid);
        var (col, row, _) = ImageMetrics.PeakNear(img, 5 * Mm, 15 * Mm, 3 * Mm);
        Assert.Equal(5 * Mm, grid.X(col), 0.4 * Mm);
        Assert.Equal(15 * Mm, grid.Z(row), 0.3 * Mm);
        // (x = -24 mm, z = 2 mm) is at -85 degrees: outside the sector.
        Assert.True(float.IsNaN(img.Sample(-24 * Mm, 2 * Mm)));
    }

    [Fact]
    public void Dynamic_receive_focusing_requires_full_matrix()
    {
        var fmc = Simulate(SinglePoint(0, 10 * Mm));
        var grid = ImageGrid.FromBounds(-5 * Mm, 5 * Mm, 5 * Mm, 15 * Mm, 0.5 * Mm);
        var imager = new SectorScanImager(C, receive: ReceiveFocus.Dynamic);
        Assert.Throws<ArgumentException>(() => imager.Reconstruct(AnalyticFmc.From(fmc, true), Probe, grid));
        var img = imager.Reconstruct(fmc, grid);
        var (col, row, _) = ImageMetrics.PeakNear(img, 0, 10 * Mm, 2 * Mm);
        Assert.Equal(10 * Mm, grid.Z(row), 0.5 * Mm);
        Assert.Equal(0, grid.X(col), 0.5 * Mm);
    }
}
