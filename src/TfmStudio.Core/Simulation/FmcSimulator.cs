using TfmStudio.Core.Physics;

namespace TfmStudio.Core.Simulation;

/// <summary>Settings of the FMC simulator.</summary>
public sealed record SimulationSettings
{
    /// <summary>Digitiser sampling rate, Hz.</summary>
    public double SamplingRate { get; init; } = 50e6;

    /// <summary>Record length; null = enough for the back-wall echo at the outermost element pair.</summary>
    public int? SampleCount { get; init; }

    /// <summary>RMS of incoherent electronic noise added to each A-scan (amplitude units).</summary>
    public double NoiseRms { get; init; } = 0.004;

    /// <summary>Reference distance for geometric spreading, m.</summary>
    public double ReferenceDistance { get; init; } = 0.01;

    /// <summary>Strength of the back-wall echo.</summary>
    public double BackWallReflectivity { get; init; } = 1.0;

    /// <summary>Reflectivity of a 1 mm radius side-drilled hole; scales with sqrt(radius).</summary>
    public double SdhReflectivityPerSqrtMm { get; init; } = 0.25;

    /// <summary>Corner-trap and tip-diffraction strengths of back-wall cracks.</summary>
    public double CrackCornerReflectivity { get; init; } = 0.45;
    public double CrackTipReflectivity { get; init; } = 0.06;

    /// <summary>Passive-axis (y) beam width, 1σ, used to weight reflectors near the scan plane, m.</summary>
    public double PassiveBeamSigma { get; init; } = 1.5e-3;

    public int Seed { get; init; } = 12345;
}

/// <summary>
/// Ray-based FMC simulator for a contact linear array on a plate.
/// <para>
/// Every reflector is reduced to point scatterers (SDHs echo from their surface; cracks give a
/// corner echo and a tip-diffracted echo; the back wall is handled with an image source). For each
/// transmitter/receiver pair the echo is a delayed copy of the probe pulse with amplitude
/// <c>R · D(θ_tx)·D(θ_rx) · sqrt(r0/r_tx)·sqrt(r0/r_rx) · exp(-α(r_tx + r_rx))</c>:
/// element directivity, 2-D cylindrical spreading per leg and material attenuation.
/// Random grain scatterers add coherent microstructural noise; Gaussian electronic noise is added last.
/// </para>
/// Reciprocity is exploited: echoes are computed for tx ≤ rx and mirrored, so the noise-free
/// part of the matrix is exactly symmetric, as it is for real FMC data.
/// </summary>
public sealed class FmcSimulator
{
    private readonly Specimen _specimen;
    private readonly SimulationSettings _settings;
    private readonly (double X, double Y, double Z, double R)[] _grains;

    public FmcSimulator(Specimen specimen, SimulationSettings? settings = null)
    {
        _specimen = specimen;
        _settings = settings ?? new SimulationSettings();
        _grains = CreateGrains(specimen, _settings.Seed);
    }

    public Specimen Specimen => _specimen;
    public SimulationSettings Settings => _settings;

    private static (double, double, double, double)[] CreateGrains(Specimen s, int seed)
    {
        if (s.GrainDensity <= 0 || s.GrainReflectivity <= 0) return [];
        var rng = new Random(seed);
        double volumeMm3 = (s.XMax - s.XMin) * (s.YMax - s.YMin) * s.Thickness * 1e9;
        int n = (int)(volumeMm3 * s.GrainDensity);
        var g = new (double, double, double, double)[n];
        for (int i = 0; i < n; i++)
        {
            double x = s.XMin + rng.NextDouble() * (s.XMax - s.XMin);
            double y = s.YMin + rng.NextDouble() * (s.YMax - s.YMin);
            double z = 0.5e-3 + rng.NextDouble() * (s.Thickness - 1.0e-3);
            g[i] = (x, y, z, s.GrainReflectivity * Gaussian(rng));
        }
        return g;
    }

    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Default record length: back-wall echo between the outermost elements plus margin.</summary>
    public int DefaultSampleCount(LinearArray array)
    {
        double c = _specimen.Material.LongitudinalVelocity;
        double path = Math.Sqrt(Math.Pow(array.ActiveAperture, 2) + Math.Pow(2 * _specimen.Thickness, 2));
        var pulse = new Pulse(array.CenterFrequency, array.FractionalBandwidth);
        double t = path / c + 2 * pulse.HalfDuration + 1e-6;
        return (int)Math.Ceiling(t * _settings.SamplingRate);
    }

    /// <summary>Point scatterers seen by the probe in the scan plane at encoder position <paramref name="y"/>.</summary>
    internal List<(double X, double Z, double Amp, double TimeShift)> ScatterersAt(double y, double probeX, double halfWidth)
    {
        var s = _settings;
        var list = new List<(double, double, double, double)>();
        double c = _specimen.Material.LongitudinalVelocity;
        foreach (var r in _specimen.Reflectors)
        {
            if (!r.IsPresentAt(y)) continue;
            switch (r)
            {
                case SideDrilledHole h:
                    double radius = h.Diameter / 2;
                    // Echo comes from the hole surface: each leg is shorter by the radius.
                    list.Add((h.X, h.Z, s.SdhReflectivityPerSqrtMm * Math.Sqrt(radius * 1e3), -2 * radius / c));
                    break;
                case PointFlaw p:
                    list.Add((p.X, p.Z, p.Reflectivity, 0));
                    break;
                case BackWallCrack k:
                    list.Add((k.X, _specimen.Thickness - 0.05e-3, s.CrackCornerReflectivity, 0));
                    list.Add((k.X, _specimen.Thickness - k.Height, s.CrackTipReflectivity, 0));
                    break;
            }
        }
        double sigma = s.PassiveBeamSigma;
        foreach (var (gx, gy, gz, gr) in _grains)
        {
            double dy = gy - y;
            if (Math.Abs(dy) > 3 * sigma || Math.Abs(gx - probeX) > halfWidth) continue;
            list.Add((gx, gz, gr * Math.Exp(-dy * dy / (2 * sigma * sigma)), 0));
        }
        return list;
    }

    /// <summary>Simulates one FMC acquisition with the probe at (<paramref name="array"/>.CenterX, y).</summary>
    public FmcData Acquire(LinearArray array, double y = 0, int? noiseSeed = null)
    {
        var s = _settings;
        int n = array.ElementCount;
        int ns = s.SampleCount ?? DefaultSampleCount(array);
        var fmc = new FmcData(array, s.SamplingRate, ns);
        var pulse = new Pulse(array.CenterFrequency, array.FractionalBandwidth);
        double c = _specimen.Material.LongitudinalVelocity;
        double lambda = c / array.CenterFrequency;
        double alpha = _specimen.Material.AttenuationNeperPerMetre(array.CenterFrequency);
        double fs = s.SamplingRate;
        double r0 = s.ReferenceDistance;
        double[] ex = array.ElementPositions();

        double halfWidth = array.ActiveAperture / 2 + 2.5 * _specimen.Thickness;
        var scatterers = ScatterersAt(y, array.CenterX, halfWidth);
        int m = scatterers.Count;

        // Per scatterer, per element: one-way time and amplitude factor of each leg.
        var legT = new double[m * n];
        var legA = new double[m * n];
        for (int k = 0; k < m; k++)
        {
            var (px, pz, _, _) = scatterers[k];
            for (int e = 0; e < n; e++)
            {
                double dx = px - ex[e];
                double r = Math.Sqrt(dx * dx + pz * pz);
                double d = array.Directivity(dx / r, pz / r, lambda);
                legT[k * n + e] = r / c;
                legA[k * n + e] = d * Math.Sqrt(r0 / r) * Math.Exp(-alpha * r);
            }
        }

        float[] data = fmc.Data;
        Parallel.For(0, n, tx =>
        {
            var buffer = new float[ns];
            for (int rx = tx; rx < n; rx++)
            {
                Array.Clear(buffer);
                for (int k = 0; k < m; k++)
                {
                    var (_, _, amp, shift) = scatterers[k];
                    double tau = legT[k * n + tx] + legT[k * n + rx] + shift;
                    double a = amp * legA[k * n + tx] * legA[k * n + rx];
                    AddEcho(buffer, pulse, tau, a, fs);
                }
                if (_specimen.HasBackWall)
                {
                    double dx = ex[rx] - ex[tx];
                    double path = Math.Sqrt(dx * dx + 4 * _specimen.Thickness * _specimen.Thickness);
                    double sin = Math.Abs(dx) / path, cos = 2 * _specimen.Thickness / path;
                    double d = array.Directivity(sin, cos, lambda);
                    double a = -s.BackWallReflectivity * d * d * Math.Sqrt(r0 / path) * Math.Exp(-alpha * path);
                    AddEcho(buffer, pulse, path / c, a, fs);
                }
                buffer.CopyTo(data, fmc.Offset(tx, rx));
                if (rx != tx) buffer.CopyTo(data, fmc.Offset(rx, tx));
            }
        });

        // Deterministic per-position seed (HashCode.Combine is randomised per process, so it would make runs irreproducible).
        if (s.NoiseRms > 0) AddNoise(data, s.NoiseRms, noiseSeed ?? unchecked(s.Seed * 486187739 + (int)Math.Round(y * 1e6)));
        return fmc;
    }

    private static void AddEcho(float[] buffer, Pulse pulse, double tau, double amplitude, double fs)
    {
        int i0 = Math.Max(0, (int)Math.Ceiling((tau - pulse.HalfDuration) * fs));
        int i1 = Math.Min(buffer.Length - 1, (int)Math.Floor((tau + pulse.HalfDuration) * fs));
        float a = (float)amplitude;
        for (int i = i0; i <= i1; i++) buffer[i] += a * pulse.Lookup(i / fs - tau);
    }

    private static void AddNoise(float[] data, double rms, int seed)
    {
        const int chunk = 1 << 16;
        int chunks = (data.Length + chunk - 1) / chunk;
        Parallel.For(0, chunks, ci =>
        {
            var rng = new Random(unchecked(seed * 31 + ci * 7919));
            int end = Math.Min(data.Length, (ci + 1) * chunk);
            for (int i = ci * chunk; i < end; i++) data[i] += (float)(rms * Gaussian(rng));
        });
    }
}
