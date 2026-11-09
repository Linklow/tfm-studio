using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TfmStudio.Core.Imaging;
using TfmStudio.Core.Physics;

namespace TfmStudio.Core.Acquisition;

/// <summary>What the pipeline reconstructs for each frame. Immutable; swap the whole record to change settings.</summary>
public sealed record ImagingOptions
{
    /// <summary>Image grid relative to the probe centre (x = 0).</summary>
    public required ImageGrid Grid { get; init; }

    /// <summary>Longitudinal velocity used for the delay laws, m/s (the operator's calibration).</summary>
    public double Velocity { get; init; } = Material.Steel.LongitudinalVelocity;

    public bool Tfm { get; init; } = true;
    public bool SectorScan { get; init; } = true;
    public DasKernel Kernel { get; init; } = DasKernel.Auto;
    public double SectorMinAngleDeg { get; init; } = -45;
    public double SectorMaxAngleDeg { get; init; } = 45;
    public double SectorFocalRange { get; init; } = 0.015;

    /// <summary>Depth window projected into the C-scan (top view), m.</summary>
    public double CScanZMin { get; init; } = 0.001;
    public double CScanZMax { get; init; } = 0.0285;
}

/// <summary>Reconstructed images of one frame plus its C-scan line and timings.</summary>
public sealed record ProcessedFrame(
    FmcFrame Source,
    ImageFrame? Tfm,
    ImageFrame? Sector,
    float[] CScanLine,
    TimeSpan HilbertTime,
    TimeSpan ProcessingTime);

/// <summary>
/// Acquisition → analytic signal → TFM / S-scan → C-scan line. In <see cref="RunAsync"/> acquisition
/// and processing overlap: the instrument fills a bounded channel while the previous frame is being
/// reconstructed. <see cref="Options"/> may be replaced at any time (for example from the UI); the
/// next frame picks the new settings up, and imagers and delay tables are rebuilt only when needed.
/// </summary>
public sealed class InspectionPipeline
{
    private readonly IUltrasonicInstrument _instrument;
    private ImagingOptions _options;
    private ImagingOptions? _builtFor;
    private TfmImager? _tfm;
    private SectorScanImager? _sector;

    public InspectionPipeline(IUltrasonicInstrument instrument, ImagingOptions options)
    {
        _instrument = instrument;
        _options = options;
    }

    public ImagingOptions Options
    {
        get => Volatile.Read(ref _options);
        set => Volatile.Write(ref _options, value ?? throw new ArgumentNullException(nameof(value)));
    }

    private void EnsureImagers(ImagingOptions o)
    {
        var b = _builtFor;
        if (b is null || b.Velocity != o.Velocity || b.Kernel != o.Kernel)
        {
            _tfm = new TfmImager(o.Velocity, o.Kernel);
            _sector = null;
        }
        if (_sector is null || b is null || b.SectorFocalRange != o.SectorFocalRange
            || b.SectorMinAngleDeg != o.SectorMinAngleDeg || b.SectorMaxAngleDeg != o.SectorMaxAngleDeg)
        {
            _sector = new SectorScanImager(o.Velocity, o.SectorMinAngleDeg, o.SectorMaxAngleDeg, 1,
                o.SectorFocalRange, ReceiveFocus.FocalLaw, o.Kernel);
        }
        _builtFor = o;
    }

    /// <summary>Processes one frame. Not thread-safe: call from one consumer at a time.</summary>
    public ProcessedFrame Process(FmcFrame frame)
    {
        var o = Options;
        EnsureImagers(o);
        var total = Stopwatch.StartNew();
        var probe = frame.Data.Array;
        var grid = o.Grid.Shifted(probe.CenterX);
        var hilbert = Stopwatch.StartNew();
        var signals = AnalyticFmc.From(frame.Data, halfMatrix: true);
        var hilbertTime = hilbert.Elapsed;
        var tfm = o.Tfm ? _tfm!.Reconstruct(signals, probe, grid) : null;
        var sector = o.SectorScan || !o.Tfm ? _sector!.Reconstruct(signals, probe, grid) : null;
        var line = CScanLine(tfm ?? sector!, o.CScanZMin, o.CScanZMax);
        return new ProcessedFrame(frame, tfm, sector, line, hilbertTime, total.Elapsed);
    }

    /// <summary>Max amplitude over the depth window for every image column (one C-scan line).</summary>
    public static float[] CScanLine(ImageFrame image, double zMin, double zMax)
    {
        var g = image.Grid;
        var line = new float[g.Width];
        for (int r = 0; r < g.Height; r++)
        {
            double z = g.Z(r);
            if (z < zMin || z > zMax) continue;
            for (int c = 0; c < g.Width; c++)
            {
                float v = image[c, r];
                if (v > line[c]) line[c] = v;
            }
        }
        return line;
    }

    public async IAsyncEnumerable<ProcessedFrame> RunAsync(ScanPlan plan, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<FmcFrame>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in _instrument.ScanAsync(plan, cancellationToken).ConfigureAwait(false))
                    await channel.Writer.WriteAsync(f, cancellationToken).ConfigureAwait(false);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return await Task.Run(() => Process(frame), cancellationToken).ConfigureAwait(false);
        await producer.ConfigureAwait(false);
    }
}

/// <summary>Top view (x–y) built line by line from the processed frames.</summary>
public sealed class CScan
{
    public CScan(ImageGrid grid, ScanPlan plan)
    {
        Grid = grid;
        Plan = plan;
        Values = new float[plan.FrameCount * grid.Width];
    }

    public ImageGrid Grid { get; }
    public ScanPlan Plan { get; }

    /// <summary>Row-major [scan index][x column].</summary>
    public float[] Values { get; }

    public int Width => Grid.Width;
    public int Height => Plan.FrameCount;

    public void SetLine(int index, ReadOnlySpan<float> line) => line.CopyTo(Values.AsSpan(index * Grid.Width, Grid.Width));
}
