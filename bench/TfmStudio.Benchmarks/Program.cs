using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using TfmStudio.Core;
using TfmStudio.Core.Imaging;
using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;

BenchmarkSwitcher.FromAssembly(typeof(DelayAndSumBenchmarks).Assembly).Run(args);

/// <summary>
/// One 5L64 FMC frame (64 x 64 x 702 samples) over the 35.6k-pixel default grid.
/// Run: dotnet run -c Release --project bench/TfmStudio.Benchmarks -- --filter *
/// </summary>
[MemoryDiagnoser]
public class DelayAndSumBenchmarks
{
    private AnalyticFmc _full = null!, _half = null!;
    private DelayTable _table = null!;
    private float[] _image = null!;
    private FmcData _fmc = null!;

    [GlobalSetup]
    public void Setup()
    {
        var probe = LinearArray.Probe5L64;
        var specimen = Scenarios.TestPlate();
        _fmc = new FmcSimulator(specimen).Acquire(probe, 0.16);
        _full = AnalyticFmc.From(_fmc, halfMatrix: false);
        _half = AnalyticFmc.From(_fmc, halfMatrix: true);
        var grid = Scenarios.DefaultGrid();
        _table = DelayTable.OneWay(probe, grid, specimen.Material.LongitudinalVelocity, _fmc.SamplingRate);
        _image = new float[grid.PixelCount];
    }

    [Benchmark(Baseline = true, Description = "Full matrix, scalar, parallel")]
    public void FullScalarParallel() => DelayAndSum.Run(_full, _table, _table, _image, DasKernel.Parallel);

    [Benchmark(Description = "Full matrix, AVX2 gather")]
    public void FullGather() => DelayAndSum.Run(_full, _table, _table, _image, DasKernel.Avx2Gather);

    [Benchmark(Description = "Full matrix, AVX2 I/Q loads")]
    public void FullInterleaved() => DelayAndSum.Run(_full, _table, _table, _image, DasKernel.Avx2Interleaved);

    [Benchmark(Description = "Half matrix, AVX2 I/Q loads")]
    public void HalfInterleaved() => DelayAndSum.Run(_half, _table, _table, _image, DasKernel.Avx2Interleaved);

    [Benchmark(Description = "Analytic signal, half matrix (2080 FFT pairs)")]
    public AnalyticFmc Hilbert() => AnalyticFmc.From(_fmc, halfMatrix: true);
}
