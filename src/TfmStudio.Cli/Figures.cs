using System.Globalization;
using System.Text;
using ScottPlot;
using TfmStudio.Core;
using TfmStudio.Core.Acquisition;
using TfmStudio.Core.Imaging;
using TfmStudio.Core.Measurement;
using TfmStudio.Core.Physics;
using TfmStudio.Core.Simulation;
using TfmStudio.Plotting;

namespace TfmStudio.Cli;

internal static class Figures_
{
    private const double Mm = 1e-3;
    private static readonly Color Series1 = Color.FromHex("#2a78d6");
    private static readonly Color Series2 = Color.FromHex("#eb6834");
    private static readonly Color Series3 = Color.FromHex("#1baf7a");
    private static readonly Color Ink = Color.FromHex("#0b0b0b");
    private static readonly Color Ink2 = Color.FromHex("#52514e");
    private static readonly Color Muted = Color.FromHex("#898781");
    private static readonly Color Grid = Color.FromHex("#e1e0d9");
    private static readonly Color Surface = Color.FromHex("#fcfcfb");

    private static Plot NewPlot()
    {
        var plt = new Plot();
        plt.FigureBackground.Color = Surface;
        plt.DataBackground.Color = Surface;
        plt.Grid.MajorLineColor = Grid;
        plt.Axes.Color(Ink2);
        return plt;
    }

    public static void HeatmapFigure(ImageFrame img, string title, string path, (double X0, double X1, double Z0, double Z1)? view = null,
        double dynamicRange = 40, IEnumerable<(double X, double Z)>? truth = null, int width = 640, int height = 520)
    {
        var g = img.Grid;
        float max = img.Max();
        var data = new double[g.Height, g.Width];
        for (int r = 0; r < g.Height; r++)
            for (int c = 0; c < g.Width; c++)
            {
                float a = img[c, r];
                data[r, c] = float.IsNaN(a) ? double.NaN : Math.Max(20 * Math.Log10(Math.Max(a, 1e-12f) / max), -dynamicRange);
            }
        var plt = NewPlot();
        var hm = plt.Add.Heatmap(data);
        hm.Colormap = new AmplitudeColormap();
        hm.ManualRange = new ScottPlot.Range(-dynamicRange, 0);
        // ScottPlot draws data row 0 at the Extent's bottom edge; with the inverted depth axis that is the top of the screen.
        hm.Extent = new CoordinateRect(
            (g.XMin - g.Dx / 2) / Mm, (g.XMax + g.Dx / 2) / Mm,
            (g.ZMin - g.Dz / 2) / Mm, (g.ZMax + g.Dz / 2) / Mm);
        var cb = plt.Add.ColorBar(hm);
        cb.Label = "dB re. image maximum";
        if (truth is not null)
            foreach (var (x, z) in truth)
            {
                var m = plt.Add.Marker(x / Mm, z / Mm, MarkerShape.OpenCircle, 14);
                m.Color = Series2;
                m.MarkerLineWidth = 1.5f;
            }
        var v = view ?? (g.XMin, g.XMax, g.ZMin, g.ZMax);
        plt.Axes.SetLimits(v.X0 / Mm, v.X1 / Mm, v.Z1 / Mm, v.Z0 / Mm); // depth grows downwards
        plt.Title(title);
        plt.XLabel("x, mm");
        plt.YLabel("depth, mm");
        plt.SavePng(path, width, height);
    }

    /// <summary>Lateral profile: max over a depth band, in dB re. the profile maximum.</summary>
    private static (double[] X, double[] Db) LateralProfile(ImageFrame img, double z0, double z1, double x0, double x1)
    {
        var g = img.Grid;
        var xs = new List<double>();
        var vs = new List<double>();
        for (int c = 0; c < g.Width; c++)
        {
            double x = g.X(c);
            if (x < x0 || x > x1) continue;
            float m = 0;
            for (int r = 0; r < g.Height; r++)
            {
                double z = g.Z(r);
                if (z < z0 || z > z1) continue;
                float v = img[c, r];
                if (v > m) m = v;
            }
            xs.Add(x / Mm);
            vs.Add(m);
        }
        double max = vs.Max();
        return (xs.ToArray(), vs.Select(v => 20 * Math.Log10(Math.Max(v, 1e-12) / max)).ToArray());
    }

    /// <summary>Profile value midway between two lateral positions relative to the weaker of the two, dB.</summary>
    private static double ProfileDip((double[] X, double[] Db) p, double xa, double xb)
    {
        double At(double x) => p.Db[Array.IndexOf(p.X, p.X.MinBy(v => Math.Abs(v - x)))];
        return At((xa + xb) / 2 / Mm) - Math.Min(At(xa / Mm), At(xb / Mm));
    }

    public static int Render(string dir)
    {
        Directory.CreateDirectory(dir);
        var probe = LinearArray.Probe5L64;
        var specimen = Scenarios.TestPlate();
        double c = specimen.Material.LongitudinalVelocity;
        double lambda = specimen.Material.Wavelength(probe.CenterFrequency);
        var sim = new FmcSimulator(specimen);
        var grid = Scenarios.DefaultGrid();
        var tfm = new TfmImager(c);
        var s15 = new SectorScanImager(c, focalRange: 15 * Mm);
        var s22 = new SectorScanImager(c, focalRange: 22 * Mm);
        var md = new StringBuilder();
        md.AppendLine("# TFM vs phased-array S-scan: measured on simulated FMC").AppendLine();
        md.AppendLine($"Probe 5L64 (5 MHz, 64 elements, 0.6 mm pitch), carbon steel 30 mm, image {grid.Width}x{grid.Height} px at 0.2 mm, " +
                      "S-scan -45..45 deg in 1 deg steps, transmit = receive focal law.").AppendLine();

        // 1) Resolution: two Ø1 mm SDHs 1.5 mm apart at 22 mm depth.
        (double X, double Z) a = (-5.0 * Mm, 22 * Mm), b = (-3.5 * Mm, 22 * Mm);
        var pairFmc = sim.Acquire(probe, 160 * Mm);
        var pairSig = AnalyticFmc.From(pairFmc, halfMatrix: true);
        var pT = tfm.Reconstruct(pairSig, probe, grid);
        var p15 = s15.Reconstruct(pairSig, probe, grid);
        var p22 = s22.Reconstruct(pairSig, probe, grid);
        var zoom = (-12 * Mm, 4 * Mm, 15 * Mm, 29 * Mm);
        HeatmapFigure(pT, "TFM: two Ø1 mm SDHs, 1.5 mm apart", Path.Combine(dir, "pair_tfm.png"), zoom, 30, [a, b], 560, 460);
        HeatmapFigure(p15, "S-scan, focus 15 mm: same frame", Path.Combine(dir, "pair_sscan.png"), zoom, 30, [a, b], 560, 460);
        var profiles = new[] { ("TFM", pT, Series1), ("S-scan, focus 15 mm", p15, Series2), ("S-scan, focus 22 mm", p22, Series3) }
            .Select(t => (t.Item1, LateralProfile(t.Item2, 21 * Mm, 23 * Mm, -10 * Mm, 1.5 * Mm), t.Item3)).ToList();
        var plt = NewPlot();
        foreach (var (label, prof, color) in profiles)
        {
            var sp = plt.Add.Scatter(prof.X, prof.Db);
            sp.LegendText = label;
            sp.Color = color;
            sp.LineWidth = 2;
            sp.MarkerSize = 0;
        }
        foreach (var x in new[] { a.X, b.X })
        {
            var vl = plt.Add.VerticalLine(x / Mm);
            vl.Color = Muted;
            vl.LinePattern = LinePattern.Dashed;
            vl.LineWidth = 1;
        }
        plt.Axes.SetLimitsY(-30, 2);
        plt.Title("Lateral profile through the SDH pair (depth 21-23 mm); dashed = true positions");
        plt.XLabel("x, mm");
        plt.YLabel("amplitude, dB");
        plt.ShowLegend(Alignment.LowerLeft);
        plt.SavePng(Path.Combine(dir, "pair_profile.png"), 760, 420);

        // 2) Crack-tip diffraction: 6 mm back-wall-breaking crack.
        var crackFmc = sim.Acquire(probe, 110 * Mm);
        var crackSig = AnalyticFmc.From(crackFmc, halfMatrix: true);
        var cT = tfm.Reconstruct(crackSig, probe, grid);
        var c15 = s15.Reconstruct(crackSig, probe, grid);
        var cview = (-10 * Mm, 18 * Mm, 14 * Mm, 32 * Mm);
        (double X, double Z) tip = (4 * Mm, 24 * Mm);
        HeatmapFigure(cT, "TFM: 6 mm root crack (tip diffraction + corner)", Path.Combine(dir, "crack_tfm.png"), cview, 40, [tip], 560, 460);
        HeatmapFigure(c15, "S-scan, focus 15 mm: same crack", Path.Combine(dir, "crack_sscan.png"), cview, 40, [tip], 560, 460);

        // 3) Full frames for context.
        var refFmc = sim.Acquire(probe, 20 * Mm);
        var refSig = AnalyticFmc.From(refFmc, halfMatrix: true);
        HeatmapFigure(tfm.Reconstruct(refSig, probe, grid), "TFM frame at y = 20 mm: Ø2 mm SDH + back wall", Path.Combine(dir, "frame_tfm.png"), null, 40, null, 640, 500);

        // 4) Classic pulse-echo A-scan with gates (element over the reference SDH).
        AScanFigure(refFmc, specimen, Path.Combine(dir, "ascan.png"));

        // Metrics.
        var c22 = s22.Reconstruct(crackSig, probe, grid);
        var methods = new[] { ("TFM", pT, cT), ("S-scan, focus 15 mm", p15, c15), ("S-scan, focus 22 mm", p22, c22) };
        string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);
        var table = new List<(string Metric, string[] Values)>
        {
            ("Dip between SDHs 1.5 mm apart on the lateral profile, dB (resolved if < -6)",
                methods.Select(m => F(ProfileDip(LateralProfile(m.Item2, 21 * Mm, 23 * Mm, -10 * Mm, 1.5 * Mm), a.X, b.X))).ToArray()),
            ("-6 dB lateral width of SDH A, mm",
                methods.Select(m => F(ImageMetrics.SizeAt(m.Item2, a.X, a.Z, 0.4 * Mm).Length / Mm, "F2")).ToArray()),
            ("Array performance indicator of SDH A (-6 dB area / λ², lower is sharper)",
                methods.Select(m => F(ImageMetrics.ArrayPerformanceIndicator(m.Item2, a.X, a.Z, lambda, 0.4 * Mm), "F2")).ToArray()),
            ("Crack height from tip diffraction, mm (true 6.0)",
                methods.Select(m => F(specimen.Thickness / Mm - m.Item3.Grid.Z(ImageMetrics.PeakNear(m.Item3, tip.X, tip.Z, 1.5 * Mm).Row) / Mm)).ToArray()),
            ("Crack-tip signal-to-grain-noise ratio, dB",
                methods.Select(m => F(ImageMetrics.SignalToNoise(m.Item3, tip.X, tip.Z, (8 * Mm, 18 * Mm, 4 * Mm, 20 * Mm)))).ToArray()),
        };
        md.AppendLine("| Metric | " + string.Join(" | ", methods.Select(m => m.Item1)) + " |");
        md.AppendLine("|---|" + string.Concat(methods.Select(_ => "---:|")));
        foreach (var (metric, values) in table) md.AppendLine($"| {metric} | {string.Join(" | ", values)} |");
        md.AppendLine();
        File.WriteAllText(Path.Combine(dir, "results.md"), md.ToString());
        Console.WriteLine(md.ToString());
        return 0;
    }

    private static void AScanFigure(FmcData fmc, Specimen specimen, string path)
    {
        var probe = fmc.Array;
        int e = Enumerable.Range(0, probe.ElementCount).MinBy(i => Math.Abs(probe.ElementX(i) - (-8 * Mm)));
        var raw = fmc.AScan(e, e).ToArray();
        var env = TfmStudio.Core.Dsp.Hilbert.Envelope(raw);
        double c = specimen.Material.LongitudinalVelocity;
        // Normalise so that the back-wall echo is 80 % full-screen height, as an operator would set the gain.
        var bwGate = new Gate(9.0e-6, 2.5e-6, 0);
        float bw = (float)bwGate.Evaluate(env, fmc.SamplingRate).Amplitude;
        var pct = env.Select(v => 80.0 * v / bw).ToArray();
        var t = Enumerable.Range(0, pct.Length).Select(i => i / fmc.SamplingRate * 1e6).ToArray();
        var gateA = new Gate(2.5e-6, 2.5e-6, 20, GateTrigger.Peak);
        var gateB = new Gate(9.0e-6, 2.5e-6, 20, GateTrigger.Peak);
        var pctF = pct.Select(v => (float)v).ToArray();
        var ra = gateA.Evaluate(pctF, fmc.SamplingRate);
        var rb = gateB.Evaluate(pctF, fmc.SamplingRate);

        var plt = NewPlot();
        var sp = plt.Add.Scatter(t, pct);
        sp.Color = Ink2;
        sp.MarkerSize = 0;
        sp.LineWidth = 1.5f;
        sp.LegendText = $"element {e + 1} pulse-echo, envelope";
        foreach (var (gate, res, color, name) in new[] { (gateA, ra, Series2, "A"), (gateB, rb, Series1, "B") })
        {
            var line = plt.Add.Line(gate.Start * 1e6, gate.Threshold, gate.End * 1e6, gate.Threshold);
            line.Color = color;
            line.LineWidth = 4;
            line.LegendText = $"gate {name}: {res.Amplitude:F0} % FSH, depth {Gate.Depth(res.Time, c) / Mm:F1} mm";
        }
        plt.Axes.SetLimits(0, t[^1], 0, 100);
        plt.Title("Pulse-echo A-scan over the Ø2 mm SDH (top of hole at 11 mm, wall 30 mm)");
        plt.XLabel("time, µs");
        plt.YLabel("amplitude, % FSH");
        plt.ShowLegend(Alignment.UpperCenter);
        plt.SavePng(path, 760, 380);
        Console.WriteLine($"A-scan: gate A depth {Gate.Depth(ra.Time, c) / Mm:F2} mm, gate B (thickness) {Gate.Depth(rb.Time, c) / Mm:F2} mm");
    }

    public static void CScanFigure(CScan cscan, Specimen specimen, string path)
    {
        var g = cscan.Grid;
        float max = cscan.Values.Max();
        var data = new double[cscan.Height, cscan.Width];
        for (int r = 0; r < cscan.Height; r++)
            for (int c = 0; c < cscan.Width; c++)
                data[r, c] = Math.Max(20 * Math.Log10(Math.Max(cscan.Values[r * cscan.Width + c], 1e-12f) / max), -40);
        var plt = NewPlot();
        var hm = plt.Add.Heatmap(data);
        hm.Colormap = new AmplitudeColormap();
        hm.ManualRange = new ScottPlot.Range(-40, 0);
        var plan = cscan.Plan;
        hm.Extent = new CoordinateRect(
            (g.XMin - g.Dx / 2) / Mm, (g.XMax + g.Dx / 2) / Mm,
            (plan.YStart - plan.Step / 2) / Mm, (plan.YEnd + plan.Step / 2) / Mm);
        var cb = plt.Add.ColorBar(hm);
        cb.Label = "dB re. maximum";
        foreach (var r in specimen.Reflectors.Where(r => r is not PointFlaw p || p.Label is "Pore 1" or "Inclusion"))
        {
            double x = r switch { SideDrilledHole h => h.X, PointFlaw p => p.X, BackWallCrack k => k.X, _ => 0 };
            if (r.Label.StartsWith("Pair SDH B", StringComparison.Ordinal)) continue;
            string label = r.Label.StartsWith("Pair", StringComparison.Ordinal) ? "SDH pair" : r.Label.StartsWith("Pore", StringComparison.Ordinal) ? "Porosity" : r.Label;
            double y = double.IsInfinity(r.YStart) ? plan.YStart : r.YStart;
            var txt = plt.Add.Text(label, x / Mm + 1.5, y / Mm + 2);
            txt.LabelFontColor = Ink;
            txt.LabelFontSize = 12;
        }
        plt.Axes.SetLimits((g.XMin) / Mm, (g.XMax) / Mm, plan.YEnd / Mm, plan.YStart / Mm);
        plt.Title("C-scan (top view): TFM max over depth 1-28.5 mm");
        plt.XLabel("x (index axis), mm");
        plt.YLabel("y (scan axis, encoder), mm");
        plt.SavePng(path, 620, 760);
    }
}
