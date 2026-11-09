using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TfmStudio.Core;
using TfmStudio.Core.Acquisition;
using TfmStudio.Core.Dsp;
using TfmStudio.Core.Imaging;
using TfmStudio.Core.Measurement;
using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;

namespace TfmStudio.App.ViewModels;

/// <summary>Pulse-echo A-scan of one element, normalised so that the back wall is 80 % full-screen height.</summary>
public sealed record AScanView(int Element, double[] TimeUs, double[] Rf, double[] Envelope, Gate GateA, GateResult A, Gate GateB, GateResult B);

/// <summary>Everything the view needs to draw one frame.</summary>
public sealed record FrameView(ProcessedFrame Frame, CScan CScan, AScanView AScan, Indication? Indication, double DynamicRange, double FocalDepthMm);

/// <summary>
/// Main view model. Owns the simulated instrument and the inspection pipeline. Settings bound to
/// the UI are pushed into the running pipeline immediately, so focal depth, velocity, kernel and
/// image types can be changed mid-scan. Rendering stays in the view (<see cref="FrameReady"/>).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const double Mm = 1e-3;
    private readonly Specimen _specimen = Scenarios.TestPlate();
    private readonly FmcSimulator _simulator;
    private readonly Stopwatch _fpsClock = new();
    private CancellationTokenSource? _cts;
    private InspectionPipeline? _pipeline;
    private int _framesSinceClock;
    private double _fps;
    private ProcessedFrame? _last;

    public MainViewModel()
    {
        _simulator = new FmcSimulator(_specimen);
        CScan = new CScan(Scenarios.DefaultGrid(), Plan);
    }

    public ScanPlan Plan { get; } = Scenarios.TestPlateScan();
    public CScan CScan { get; private set; }
    public string SpecimenDescription =>
        $"{_specimen.Material.Name}, {_specimen.Thickness / Mm:F0} mm wall, {_specimen.Reflectors.Count} reflectors along a {Plan.YEnd / Mm:F0} mm scan";

    public IReadOnlyList<string> Probes { get; } = ["5L64: 5 MHz, 64 el., 0.6 mm pitch", "5L32: 5 MHz, 32 el., 0.6 mm pitch"];
    public IReadOnlyList<DasKernel> Kernels { get; } = Enum.GetValues<DasKernel>();
    public IReadOnlyList<double> Speeds { get; } = [10, 20, 50, 0];
    public string CpuInfo => $"{Environment.ProcessorCount} threads, AVX2 {(DelayAndSum.SimdSupported ? "yes" : "no")}";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ElementCount))] private int _probeIndex;
    [ObservableProperty] private bool _showTfm = true;
    [ObservableProperty] private bool _showSectorScan = true;
    [ObservableProperty] private double _focalDepthMm = 15;
    [ObservableProperty] private double _dynamicRangeDb = 40;
    [ObservableProperty] private double _velocity = Material.Steel.LongitudinalVelocity;
    [ObservableProperty] private DasKernel _kernel = DasKernel.Auto;
    [ObservableProperty] private double _scanSpeedMmPerS = 20;
    [ObservableProperty] private int _aScanElement = 19;
    [ObservableProperty] private double _gateAStartUs = 2.5;
    [ObservableProperty] private double _gateAWidthUs = 6.0;
    [ObservableProperty] private double _gateBStartUs = 9.0;
    [ObservableProperty] private double _gateBWidthUs = 2.5;
    [ObservableProperty] private double _gateThresholdPct = 20;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isScanning;

    [ObservableProperty] private string _status = "Ready. Press Start to run the encoded scan.";
    [ObservableProperty] private string _gateAReadout = "-";
    [ObservableProperty] private string _gateBReadout = "-";
    [ObservableProperty] private string _indicationReadout = "-";
    [ObservableProperty] private double _positionMm;

    public bool IsIdle => !IsScanning;
    public int ElementCount => Probe.ElementCount;
    private LinearArray Probe => ProbeIndex == 0 ? LinearArray.Probe5L64 : LinearArray.Probe5L32;

    /// <summary>Raised on the UI thread after each processed frame.</summary>
    public event Action<FrameView>? FrameReady;

    partial void OnShowTfmChanged(bool value) => PushOptions();
    partial void OnShowSectorScanChanged(bool value) => PushOptions();
    partial void OnFocalDepthMmChanged(double value) => PushOptions();
    partial void OnVelocityChanged(double value) => PushOptions();
    partial void OnKernelChanged(DasKernel value) => PushOptions();
    partial void OnDynamicRangeDbChanged(double value) => Redraw();
    partial void OnAScanElementChanged(int value) => Redraw();
    partial void OnGateAStartUsChanged(double value) => Redraw();
    partial void OnGateAWidthUsChanged(double value) => Redraw();
    partial void OnGateBStartUsChanged(double value) => Redraw();
    partial void OnGateBWidthUsChanged(double value) => Redraw();
    partial void OnGateThresholdPctChanged(double value) => Redraw();
    partial void OnProbeIndexChanged(int value) => AScanElement = Math.Min(AScanElement, ElementCount);

    private ImagingOptions BuildOptions() => new()
    {
        Grid = Scenarios.DefaultGrid(),
        Velocity = Velocity,
        Tfm = ShowTfm,
        SectorScan = ShowSectorScan,
        Kernel = Kernel,
        SectorFocalRange = FocalDepthMm * Mm,
    };

    private void PushOptions()
    {
        if (_pipeline is not null) _pipeline.Options = BuildOptions();
    }

    private bool CanStart() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => ScanAsync(Plan with { Speed = ScanSpeedMmPerS * Mm });

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void Stop() => _cts?.Cancel();

    /// <summary>Runs the first <paramref name="frames"/> frames as fast as possible (screenshots, tests).</summary>
    public Task RunFramesAsync(int frames) =>
        ScanAsync(Plan with { YEnd = Plan.Position(Math.Min(frames, Plan.FrameCount) - 1), Speed = 0 });

    private async Task ScanAsync(ScanPlan plan)
    {
        IsScanning = true;
        _cts = new CancellationTokenSource();
        var instrument = new SimulatedInstrument(_simulator, Probe);
        _pipeline = new InspectionPipeline(instrument, BuildOptions());
        CScan = new CScan(Scenarios.DefaultGrid(), Plan);
        _fpsClock.Restart();
        _framesSinceClock = 0;
        try
        {
            // The awaits resume on the UI thread; the pipeline does its work on the thread pool.
            await foreach (var frame in _pipeline.RunAsync(plan, _cts.Token))
                OnFrame(frame);
            Status = $"Scan complete: {plan.FrameCount} frames. " + Status;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped. " + Status;
        }
        finally
        {
            IsScanning = false;
            _pipeline = null;
        }
    }

    private void OnFrame(ProcessedFrame f)
    {
        _last = f;
        CScan.SetLine(f.Source.Index, f.CScanLine);
        PositionMm = f.Source.Y / Mm;
        _framesSinceClock++;
        if (_fpsClock.Elapsed.TotalSeconds >= 1)
        {
            _fps = _framesSinceClock / _fpsClock.Elapsed.TotalSeconds;
            _fpsClock.Restart();
            _framesSinceClock = 0;
        }
        string Ms(TimeSpan? t) => t is { } v ? $"{v.TotalMilliseconds,4:F0} ms" : "  off";
        Status = $"y {f.Source.Y / Mm,5:F0} mm | frame {f.Source.Index + 1,3}/{Plan.FrameCount} | FMC {f.Source.Data.ElementCount}x{f.Source.Data.ElementCount}x{f.Source.Data.SampleCount} " +
                 $"| acquire {Ms(f.Source.AcquisitionTime)} | analytic {Ms(f.HilbertTime)} | TFM {Ms(f.Tfm?.ComputeTime)} | S-scan {Ms(f.Sector?.ComputeTime)} " +
                 $"| {_fps:F1} frames/s | kernel {DelayAndSum.Resolve(Kernel)}";
        Redraw();
    }

    private void Redraw()
    {
        if (_last is not { } f) return;
        var ascan = BuildAScan(f.Source.Data);
        GateAReadout = ascan.A.Triggered
            ? $"A: {ascan.A.Amplitude,5:F0} % FSH, depth {Gate.Depth(ascan.A.Time, Velocity) / Mm:F1} mm"
            : "A: no signal above threshold";
        GateBReadout = ascan.B.Triggered
            ? $"B: wall thickness {Gate.Depth(ascan.B.Time, Velocity) / Mm:F2} mm"
            : "B: no back-wall echo";
        Indication? indication = null;
        var img = f.Tfm ?? f.Sector;
        if (img is not null)
        {
            indication = StrongestIndication(img);
            IndicationReadout = indication is { } i
                ? $"{img.Method} peak at x {i.X / Mm:F1} mm, depth {i.Z / Mm:F1} mm\n-6 dB size {i.Length / Mm:F1} x {i.Height / Mm:F1} mm"
                : "no indication above the back-wall gate";
        }
        FrameReady?.Invoke(new FrameView(f, CScan, ascan, indication, DynamicRangeDb, FocalDepthMm));
    }

    /// <summary>Strongest indication shallower than the back wall (C-scan gate).</summary>
    private static Indication? StrongestIndication(ImageFrame img)
    {
        var g = img.Grid;
        int best = -1;
        float max = 0;
        for (int r = 0; r < g.Height; r++)
        {
            if (g.Z(r) > 0.0285) break;
            for (int c = 0; c < g.Width; c++)
            {
                float v = img[c, r];
                if (v > max) { max = v; best = r * g.Width + c; }
            }
        }
        if (best < 0) return null;
        return ImageMetrics.SizeAt(img, g.X(best % g.Width), g.Z(best / g.Width), g.Dx);
    }

    private AScanView BuildAScan(FmcData fmc)
    {
        int e = Math.Clamp(AScanElement - 1, 0, fmc.ElementCount - 1);
        var rf = fmc.AScan(e, e).ToArray();
        var env = Hilbert.Envelope(rf);
        var gateB = new Gate(GateBStartUs * 1e-6, GateBWidthUs * 1e-6, 0);
        float bw = (float)Math.Max(gateB.Evaluate(env, fmc.SamplingRate).Amplitude, 1e-9);
        double scale = 80.0 / bw;
        var envPct = env.Select(v => v * scale).ToArray();
        var rfPct = rf.Select(v => v * scale).ToArray();
        var time = Enumerable.Range(0, rf.Length).Select(i => fmc.TimeOfSample(i) * 1e6).ToArray();
        var envF = envPct.Select(v => (float)v).ToArray();
        var a = new Gate(GateAStartUs * 1e-6, GateAWidthUs * 1e-6, GateThresholdPct);
        var b = gateB with { Threshold = GateThresholdPct };
        return new AScanView(e + 1, time, rfPct, envPct, a, a.Evaluate(envF, fmc.SamplingRate), b, b.Evaluate(envF, fmc.SamplingRate));
    }
}
