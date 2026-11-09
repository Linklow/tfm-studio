using System.Diagnostics;
using TfmStudio.Core;
using TfmStudio.Core.Acquisition;
using TfmStudio.Core.Imaging;
using TfmStudio.Core.Measurement;
using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;

namespace TfmStudio.Cli;

internal static class Commands
{
    private const double Mm = 1e-3;

    public static int Usage()
    {
        Console.WriteLine("""
            tfm-studio command line

              check                 simulate reference frames and print image metrics
              bench [frames]        time FMC simulation, Hilbert and every delay-and-sum kernel
              figures [dir]         render the README figures and results.md
              scan [dir]            run the full encoded scan through the pipeline, save the C-scan
            """);
        return 1;
    }

    private static readonly LinearArray Probe = LinearArray.Probe5L64;

    public static int Check()
    {
        var specimen = Scenarios.TestPlate();
        var sim = new FmcSimulator(specimen);
        var grid = Scenarios.DefaultGrid();
        double c = specimen.Material.LongitudinalVelocity;
        double lambda = specimen.Material.Wavelength(Probe.CenterFrequency);

        foreach (var (y, name, points) in new (double, string, (double X, double Z)[])[]
                 {
                     (20 * Mm, "reference SDH", [(-8 * Mm, 12 * Mm)]),
                     (160 * Mm, "SDH pair", [(-5 * Mm, 22 * Mm), (-3.5 * Mm, 22 * Mm)]),
                     (110 * Mm, "root crack", [(4 * Mm, 24 * Mm), (4 * Mm, 30 * Mm)]),
                 })
        {
            var sw = Stopwatch.StartNew();
            var fmc = sim.Acquire(Probe, y);
            var tSim = sw.Elapsed;
            sw.Restart();
            var a = AnalyticFmc.From(fmc, halfMatrix: true);
            var tHil = sw.Elapsed;
            var tfm = new TfmImager(c).Reconstruct(a, Probe, grid);
            var ss = new SectorScanImager(c, focalRange: 15 * Mm).Reconstruct(a, Probe, grid);
            Console.WriteLine($"[{name}] y={y / Mm} mm  FMC {fmc.ElementCount}x{fmc.ElementCount}x{fmc.SampleCount}  sim {tSim.TotalMilliseconds:F0} ms, " +
                              $"hilbert {tHil.TotalMilliseconds:F0} ms, TFM {tfm.ComputeTime.TotalMilliseconds:F0} ms, S-scan {ss.ComputeTime.TotalMilliseconds:F0} ms");
            foreach (var img in new[] { tfm, ss })
            {
                Console.Write($"   {img.Method,-7}");
                foreach (var p in points)
                {
                    var ind = ImageMetrics.SizeAt(img, p.X, p.Z, 1.5 * Mm);
                    Console.Write($" | peak ({ind.X / Mm:F2},{ind.Z / Mm:F2}) mm, -6dB {ind.Length / Mm:F2}x{ind.Height / Mm:F2} mm, API {ind.Area / (lambda * lambda):F2}");
                }
                if (points.Length == 2) Console.Write($" | dip {ImageMetrics.DipBetween(img, points[0], points[1]):F1} dB");
                Console.WriteLine($" | SNR {ImageMetrics.SignalToNoise(img, points[0].X, points[0].Z, (5 * Mm, 18 * Mm, 4 * Mm, 26 * Mm)):F1} dB");
            }
        }
        return 0;
    }

    public static int Bench(string[] args)
    {
        int repeats = args.Length > 0 ? int.Parse(args[0]) : 5;
        var specimen = Scenarios.TestPlate();
        var sim = new FmcSimulator(specimen);
        var grid = Scenarios.DefaultGrid();
        double c = specimen.Material.LongitudinalVelocity;
        var fmc = sim.Acquire(Probe, 160 * Mm);
        var full = AnalyticFmc.From(fmc, halfMatrix: false);
        var half = AnalyticFmc.From(fmc, halfMatrix: true);
        var table = DelayTable.OneWay(Probe, grid, c, fmc.SamplingRate);
        var image = new float[grid.PixelCount];
        Console.WriteLine($"FMC {Probe.ElementCount}x{Probe.ElementCount}x{fmc.SampleCount} @ {fmc.SamplingRate / 1e6} MHz, image {grid.Width}x{grid.Height} = {grid.PixelCount} px, {Environment.ProcessorCount} logical CPUs, AVX2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}");
        Console.WriteLine($"{"stage",-38} {"median ms",10} {"Mpx·pairs/s",12}");
        Time("FMC simulation", repeats, () => sim.Acquire(Probe, 160 * Mm));
        Time("Analytic signal (half matrix)", repeats, () => AnalyticFmc.From(fmc, true));
        foreach (var (kernel, sig, label) in new[]
                 {
                     (DasKernel.Scalar, full, "Full matrix, scalar, 1 thread"),
                     (DasKernel.Parallel, full, "Full matrix, scalar, parallel"),
                     (DasKernel.Avx2Gather, full, "Full matrix, AVX2 gather, parallel"),
                     (DasKernel.Avx2Interleaved, full, "Full matrix, AVX2 I/Q loads, parallel"),
                     (DasKernel.Parallel, half, "Half matrix, scalar, parallel"),
                     (DasKernel.Avx2Interleaved, half, "Half matrix, AVX2 I/Q loads, parallel"),
                 })
        {
            int reps = kernel == DasKernel.Scalar ? Math.Min(repeats, 2) : repeats;
            double ms = Time(label, reps, () => DelayAndSum.Run(sig, table, table, image, kernel), print: false);
            double rate = (double)grid.PixelCount * sig.PairCount / (ms / 1000) / 1e6;
            Console.WriteLine($"{label,-38} {ms,10:F1} {rate,12:F0}");
        }
        return 0;
    }

    private static double Time(string label, int repeats, Action action, bool print = true)
    {
        action(); // warm-up / JIT
        var t = new List<double>();
        for (int i = 0; i < repeats; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            t.Add(sw.Elapsed.TotalMilliseconds);
        }
        t.Sort();
        double median = t[t.Count / 2];
        if (print) Console.WriteLine($"{label,-38} {median,10:F1}");
        return median;
    }

    public static int Figures(string dir) => Figures_.Render(dir);

    public static async Task<int> ScanAsync(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "docs/figures";
        Directory.CreateDirectory(dir);
        var specimen = Scenarios.TestPlate();
        var instrument = new SimulatedInstrument(new FmcSimulator(specimen), Probe);
        var options = new ImagingOptions { Grid = Scenarios.DefaultGrid(), SectorScan = false };
        var pipeline = new InspectionPipeline(instrument, options);
        var plan = Scenarios.TestPlateScan();
        var cscan = new CScan(options.Grid, plan);
        var sw = Stopwatch.StartNew();
        await foreach (var f in pipeline.RunAsync(plan))
        {
            cscan.SetLine(f.Source.Index, f.CScanLine);
            if (f.Source.Index % 20 == 0) Console.WriteLine($"y = {f.Source.Y / Mm,5:F0} mm  acquire {f.Source.AcquisitionTime.TotalMilliseconds,5:F0} ms  process {f.ProcessingTime.TotalMilliseconds,5:F0} ms");
        }
        Console.WriteLine($"{plan.FrameCount} frames in {sw.Elapsed.TotalSeconds:F1} s ({plan.FrameCount / sw.Elapsed.TotalSeconds:F1} frames/s)");
        Figures_.CScanFigure(cscan, specimen, Path.Combine(dir, "cscan.png"));
        return 0;
    }
}
