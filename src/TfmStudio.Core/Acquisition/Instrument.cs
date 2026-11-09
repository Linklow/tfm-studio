using System.Diagnostics;
using System.Runtime.CompilerServices;
using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;

namespace TfmStudio.Core.Acquisition;

/// <summary>An encoded line scan along the y axis.</summary>
/// <param name="YStart">First encoder position, m.</param>
/// <param name="YEnd">Last encoder position, m.</param>
/// <param name="Step">Encoder step between frames, m.</param>
/// <param name="Speed">Probe speed, m/s; 0 runs as fast as the instrument can acquire.</param>
public sealed record ScanPlan(double YStart, double YEnd, double Step, double Speed = 0)
{
    public int FrameCount => (int)Math.Floor((YEnd - YStart) / Step + 1e-9) + 1;
    public double Position(int index) => YStart + index * Step;
}

/// <summary>One FMC acquired at an encoder position.</summary>
public sealed record FmcFrame(int Index, double Y, FmcData Data, TimeSpan AcquisitionTime);

/// <summary>
/// Hardware abstraction for an FMC-capable phased-array instrument. The UI and the processing
/// pipeline only see this interface, so a real driver (vendor SDK, socket protocol, file replay)
/// can replace <see cref="SimulatedInstrument"/> without touching them.
/// </summary>
public interface IUltrasonicInstrument : IAsyncDisposable
{
    string Name { get; }
    LinearArray Probe { get; }
    double SamplingRate { get; }

    /// <summary>Acquires one FMC per encoder position of <paramref name="plan"/>.</summary>
    IAsyncEnumerable<FmcFrame> ScanAsync(ScanPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>Instrument backed by <see cref="FmcSimulator"/>; paces frames like a real encoder scan.</summary>
public sealed class SimulatedInstrument(FmcSimulator simulator, LinearArray probe) : IUltrasonicInstrument
{
    public string Name => $"Simulated {probe.ElementCount}-element FMC instrument";
    public LinearArray Probe => probe;
    public double SamplingRate => simulator.Settings.SamplingRate;
    public FmcSimulator Simulator => simulator;

    public async IAsyncEnumerable<FmcFrame> ScanAsync(ScanPlan plan, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < plan.FrameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double y = plan.Position(i);
            var sw = Stopwatch.StartNew();
            var data = await Task.Run(() => simulator.Acquire(probe, y), cancellationToken).ConfigureAwait(false);
            var frame = new FmcFrame(i, y, data, sw.Elapsed);
            if (plan.Speed > 0)
            {
                var due = TimeSpan.FromSeconds((y - plan.YStart) / plan.Speed);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            yield return frame;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
