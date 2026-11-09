using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;

namespace TfmStudio.Core.Imaging;

/// <summary>
/// Total Focusing Method: every pixel is focused in transmission and reception using the full matrix.
/// Delay tables are cached per (array position, grid), so they are reused frame after frame.
/// </summary>
public sealed class TfmImager
{
    private readonly double _velocity;
    private (LinearArray Array, ImageGrid Grid, double Fs, DelayTable Table)? _cache;

    public TfmImager(double velocity, DasKernel kernel = DasKernel.Auto, bool useReciprocity = true)
    {
        _velocity = velocity;
        Kernel = kernel;
        UseReciprocity = useReciprocity;
    }

    public DasKernel Kernel { get; set; }

    /// <summary>Use the half matrix (tx ≤ rx), valid because TFM delays are symmetric in tx/rx.</summary>
    public bool UseReciprocity { get; }

    public DelayTable Delays(LinearArray array, ImageGrid grid, double samplingRate)
    {
        if (_cache is { } c && c.Array == array && c.Grid == grid && c.Fs == samplingRate) return c.Table;
        var table = DelayTable.OneWay(array, grid, _velocity, samplingRate);
        _cache = (array, grid, samplingRate, table);
        return table;
    }

    public ImageFrame Reconstruct(AnalyticFmc signals, LinearArray array, ImageGrid grid)
    {
        var table = Delays(array, grid, signals.SamplingRate);
        var image = new float[grid.PixelCount];
        var t = DelayAndSum.Run(signals, table, table, image, Kernel);
        return new ImageFrame(grid, image, "TFM") { ComputeTime = t };
    }

    public ImageFrame Reconstruct(FmcData fmc, ImageGrid grid) =>
        Reconstruct(AnalyticFmc.From(fmc, UseReciprocity), fmc.Array, grid);
}

/// <summary>Receive focusing used by the phased-array S-scan emulation.</summary>
public enum ReceiveFocus
{
    /// <summary>The same focal law as on transmission (conventional PAUT).</summary>
    FocalLaw,
    /// <summary>Dynamic depth focusing on reception (focus follows every pixel).</summary>
    Dynamic,
}

/// <summary>
/// Conventional phased-array sectorial scan (S-scan), synthesised from FMC.
/// <para>
/// Firing the elements with focal-law delays δ and receiving with delays δ' gives, by linearity,
/// <c>Σ_tx Σ_rx h_tx,rx(t − δ_tx − δ'_rx)</c>. The A-scan of beam θ is displayed along the beam
/// line, where the transmitted pulse reaches range r at <c>T(r) = MaxPath/c + (r − F)/c</c>.
/// Each pixel uses the focal law of the nearest beam angle, which is the usual scan conversion.
/// The result is what a PAUT instrument with the same probe and focal laws would show.
/// </para>
/// </summary>
public sealed class SectorScanImager
{
    private readonly double _velocity;
    private (LinearArray Array, ImageGrid Grid, double Fs, DelayTable Tx, DelayTable Rx)? _cache;

    public SectorScanImager(double velocity, double minAngleDeg = -45, double maxAngleDeg = 45, double stepDeg = 1,
        double focalRange = 0.02, ReceiveFocus receive = ReceiveFocus.FocalLaw, DasKernel kernel = DasKernel.Auto)
    {
        _velocity = velocity;
        MinAngle = minAngleDeg * Math.PI / 180;
        MaxAngle = maxAngleDeg * Math.PI / 180;
        Step = stepDeg * Math.PI / 180;
        FocalRange = focalRange;
        Receive = receive;
        Kernel = kernel;
    }

    public double MinAngle { get; }
    public double MaxAngle { get; }
    public double Step { get; }
    public double FocalRange { get; }
    public ReceiveFocus Receive { get; }
    public DasKernel Kernel { get; set; }

    public bool NeedsFullMatrix => Receive == ReceiveFocus.Dynamic;

    public (DelayTable Tx, DelayTable Rx) Delays(LinearArray array, ImageGrid grid, double samplingRate)
    {
        if (_cache is { } c && c.Array == array && c.Grid == grid && c.Fs == samplingRate) return (c.Tx, c.Rx);
        var laws = FocalLaws.Sector(array, _velocity, MinAngle, MaxAngle, Step, FocalRange);
        int n = array.ElementCount;
        var tx = new DelayTable(n, grid);
        double fs = samplingRate, c0 = array.CenterX;
        Parallel.For(0, grid.Height, j =>
        {
            double z = grid.Z(j);
            for (int i = 0; i < grid.Width; i++)
            {
                int p = j * grid.Width + i;
                double dx = grid.X(i) - c0;
                double theta = Math.Atan2(dx, z);
                if (theta < MinAngle - Step / 2 || theta > MaxAngle + Step / 2)
                {
                    for (int e = 0; e < n; e++) tx.Samples[e * grid.PixelCount + p] = DelayTable.Masked;
                    continue;
                }
                var law = laws[(int)Math.Round((theta - MinAngle) / Step)];
                double r = Math.Sqrt(dx * dx + z * z);
                double arrival = law.MaxPath / _velocity + (r - law.FocalRange) / _velocity;
                for (int e = 0; e < n; e++)
                    tx.Samples[e * grid.PixelCount + p] = (float)((arrival - law.Delays[e]) * fs);
            }
        });
        var rx = Receive == ReceiveFocus.FocalLaw ? tx : DelayTable.OneWay(array, grid, _velocity, fs);
        if (Receive == ReceiveFocus.Dynamic)
        {
            // Mask the receive table outside the sector too.
            for (int p = 0; p < grid.PixelCount; p++)
                if (tx.Samples[p] == DelayTable.Masked)
                    for (int e = 0; e < n; e++) rx.Samples[e * grid.PixelCount + p] = DelayTable.Masked;
        }
        _cache = (array, grid, samplingRate, tx, rx);
        return (tx, rx);
    }

    public ImageFrame Reconstruct(AnalyticFmc signals, LinearArray array, ImageGrid grid)
    {
        if (NeedsFullMatrix && signals.HalfMatrix)
            throw new ArgumentException("dynamic receive focusing needs full-matrix signals", nameof(signals));
        var (tx, rx) = Delays(array, grid, signals.SamplingRate);
        var image = new float[grid.PixelCount];
        var t = DelayAndSum.Run(signals, tx, rx, image, Kernel);
        for (int p = 0; p < image.Length; p++)
            if (tx.Samples[p] == DelayTable.Masked) image[p] = float.NaN;
        return new ImageFrame(grid, image, "S-scan") { ComputeTime = t };
    }

    public ImageFrame Reconstruct(FmcData fmc, ImageGrid grid) =>
        Reconstruct(AnalyticFmc.From(fmc, halfMatrix: !NeedsFullMatrix), fmc.Array, grid);
}
