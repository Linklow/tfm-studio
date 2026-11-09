using TfmStudio.Core.Acquisition;
using TfmStudio.Core.Imaging;
using TfmStudio.Core.IO;
using TfmStudio.Core.Measurement;
using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;

namespace TfmStudio.Core.Tests;

public class MeasurementAndPipelineTests
{
    private const double Mm = 1e-3;

    [Fact]
    public void Gate_reports_peak_and_flank_times()
    {
        double fs = 100e6;
        var s = new float[1000];
        for (int i = 0; i < s.Length; i++) s[i] = (float)Math.Exp(-Math.Pow((i - 400) / 20.0, 2));
        var peak = new Gate(3e-6, 2e-6, 0.5).Evaluate(s, fs);
        Assert.True(peak.Triggered);
        Assert.Equal(4e-6, peak.Time, 1e-9);
        Assert.Equal(1.0, peak.Amplitude, 1e-6);
        var flank = new Gate(3e-6, 2e-6, 0.5, GateTrigger.Flank).Evaluate(s, fs);
        // exp(-(d/20)^2) = 0.5 at d = 20*sqrt(ln 2) = 16.65 samples before the peak
        Assert.Equal((400 - 16.65) / fs, flank.Time, 0.05 / fs);
        var miss = new Gate(0, 1e-6, 0.5).Evaluate(s, fs);
        Assert.False(miss.Triggered);
        Assert.Equal(30e-3, Gate.Depth(2 * 30e-3 / 5920, 5920), 1e-12);
    }

    [Fact]
    public void Six_db_sizing_of_a_gaussian_blob()
    {
        var grid = ImageGrid.FromBounds(-5 * Mm, 5 * Mm, 0, 10 * Mm, 0.05 * Mm);
        var a = new float[grid.PixelCount];
        double sx = 0.8 * Mm, sz = 0.4 * Mm;
        for (int r = 0; r < grid.Height; r++)
            for (int c = 0; c < grid.Width; c++)
                a[r * grid.Width + c] = (float)Math.Exp(-Math.Pow(grid.X(c) - 1 * Mm, 2) / (2 * sx * sx) - Math.Pow(grid.Z(r) - 5 * Mm, 2) / (2 * sz * sz));
        var ind = ImageMetrics.SizeAt(new ImageFrame(grid, a, "test"), 1 * Mm, 5 * Mm, 1 * Mm);
        double fwhm = 2 * Math.Sqrt(2 * Math.Log(2));
        Assert.Equal(fwhm * sx, ind.Length, 0.06 * Mm);
        Assert.Equal(fwhm * sz, ind.Height, 0.06 * Mm);
        Assert.Equal(1 * Mm, ind.X, 1e-9);
    }

    [Fact]
    public async Task Pipeline_processes_an_encoded_scan_in_order()
    {
        var specimen = new Specimen(Material.Steel, 20 * Mm, [new PointFlaw("p", 0, 10 * Mm, 0.3, 2 * Mm, 4 * Mm)]);
        var instrument = new SimulatedInstrument(new FmcSimulator(specimen, new SimulationSettings { NoiseRms = 0.002 }), LinearArray.Probe5L32);
        var options = new ImagingOptions { Grid = ImageGrid.FromBounds(-8 * Mm, 8 * Mm, 2 * Mm, 18 * Mm, 0.4 * Mm), CScanZMax = 18 * Mm };
        var pipeline = new InspectionPipeline(instrument, options);
        var plan = new ScanPlan(0, 6 * Mm, 1 * Mm);
        var cscan = new CScan(options.Grid, plan);
        var frames = new List<ProcessedFrame>();
        await foreach (var f in pipeline.RunAsync(plan))
        {
            frames.Add(f);
            cscan.SetLine(f.Source.Index, f.CScanLine);
        }
        Assert.Equal(plan.FrameCount, frames.Count);
        Assert.Equal(Enumerable.Range(0, plan.FrameCount), frames.Select(f => f.Source.Index));
        Assert.All(frames, f => Assert.NotNull(f.Tfm));
        Assert.All(frames, f => Assert.NotNull(f.Sector));
        // The flaw exists only for 2 mm <= y <= 4 mm: those C-scan lines are much stronger.
        float Line(int i) => cscan.Values.AsSpan(i * cscan.Width, cscan.Width).ToArray().Max();
        Assert.True(Line(3) > 10 * Line(0));
        Assert.True(Line(3) > 10 * Line(6));
    }

    [Fact]
    public async Task Scan_can_be_cancelled()
    {
        var specimen = new Specimen(Material.Steel, 20 * Mm, []);
        var instrument = new SimulatedInstrument(new FmcSimulator(specimen), LinearArray.Probe5L32);
        using var cts = new CancellationTokenSource();
        int n = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in instrument.ScanAsync(new ScanPlan(0, 1, 1e-3), cts.Token))
                if (++n == 3) cts.Cancel();
        });
        Assert.Equal(3, n);
    }

    [Fact]
    public void Fmc_file_round_trip()
    {
        var specimen = new Specimen(Material.Steel, 15 * Mm, [new PointFlaw("p", 1 * Mm, 6 * Mm, 0.2)]);
        var fmc = new FmcSimulator(specimen).Acquire(LinearArray.Probe5L32 with { CenterX = 2 * Mm }, 0.05);
        using var ms = new MemoryStream();
        FmcFile.Write(ms, fmc, 0.05);
        ms.Position = 0;
        var (back, y) = FmcFile.Read(ms);
        Assert.Equal(0.05, y);
        Assert.Equal(fmc.Array, back.Array);
        Assert.Equal(fmc.SamplingRate, back.SamplingRate);
        Assert.Equal(fmc.Data, back.Data);
    }
}
