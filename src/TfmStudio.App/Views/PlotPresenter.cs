using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using TfmStudio.App.ViewModels;
using TfmStudio.Core.Imaging;
using TfmStudio.Plotting;

namespace TfmStudio.App.Views;

/// <summary>
/// Draws frames into the four ScottPlot controls. View-layer only: no processing here.
/// Heatmaps and colour bars are created once and then only their data is replaced, which keeps
/// rendering cheap and avoids rebuilding plot panels every frame.
/// </summary>
public sealed class PlotPresenter
{
    private const double Mm = 1e-3;
    private readonly AmplitudeColormap _colormap = new();
    private readonly ImageView _tfm, _sector, _cscan;
    private readonly WpfPlot _ascan;
    private HorizontalLine? _scanLine;

    public PlotPresenter(WpfPlot tfm, WpfPlot sector, WpfPlot cscan, WpfPlot ascan)
    {
        _tfm = new ImageView(tfm, _colormap, "TFM (total focusing method)");
        _sector = new ImageView(sector, _colormap, "Phased-array S-scan");
        _cscan = new ImageView(cscan, _colormap, "C-scan (top view)");
        _ascan = ascan;
        Theme.Style(ascan.Plot);
        ascan.Plot.Title("A-scan");
        ascan.Refresh();
    }

    public void Render(FrameView v)
    {
        var primary = v.Frame.Tfm ?? v.Frame.Sector;
        DrawImage(_tfm, v.Frame.Tfm, "TFM: focused at every pixel", v, ReferenceEquals(primary, v.Frame.Tfm));
        DrawImage(_sector, v.Frame.Sector, $"S-scan ±45°, focal law at {v.FocalDepthMm:F0} mm", v, ReferenceEquals(primary, v.Frame.Sector));
        DrawCScan(v);
        DrawAScan(v);
    }

    private static void DrawImage(ImageView view, ImageFrame? img, string title, FrameView v, bool showIndication)
    {
        view.ClearOverlays();
        if (img is null)
        {
            view.Hide(title + " (off)");
            return;
        }
        view.Show(Heatmaps.ToDb(img, v.DynamicRange), Heatmaps.DepthExtent(img.Grid), v.DynamicRange);
        if (showIndication && v.Indication is { } i)
        {
            var r = view.Plot.Plot.Add.Rectangle((i.X - i.Length / 2) / Mm - 0.5, (i.X + i.Length / 2) / Mm + 0.5,
                (i.Z - i.Height / 2) / Mm - 0.5, (i.Z + i.Height / 2) / Mm + 0.5);
            r.FillColor = Colors.Transparent;
            r.LineColor = Theme.Series2;
            r.LineWidth = 1.5f;
            view.AddOverlay(r);
        }
        var g = img.Grid;
        var plt = view.Plot.Plot;
        plt.Axes.SetLimits(g.XMin / Mm, g.XMax / Mm, g.ZMax / Mm, g.ZMin / Mm);
        plt.Title(title);
        plt.XLabel("x, mm");
        plt.YLabel("depth, mm");
        view.Plot.Refresh();
    }

    private void DrawCScan(FrameView v)
    {
        var c = v.CScan;
        _cscan.Show(Heatmaps.ToDb(c, v.DynamicRange), Heatmaps.ScanExtent(c), v.DynamicRange);
        var plt = _cscan.Plot.Plot;
        _scanLine ??= plt.Add.HorizontalLine(0, 1.5f, Theme.Series2);
        _scanLine.Y = v.Frame.Source.Y / Mm;
        plt.Axes.SetLimits(c.Grid.XMin / Mm, c.Grid.XMax / Mm, c.Plan.YEnd / Mm, c.Plan.YStart / Mm);
        plt.Title("C-scan (top view), depth 1-28.5 mm");
        plt.XLabel("x, mm");
        plt.YLabel("scan y, mm");
        _cscan.Plot.Refresh();
    }

    private void DrawAScan(FrameView v)
    {
        var plt = _ascan.Plot;
        plt.Clear();
        var a = v.AScan;
        var rf = plt.Add.SignalXY(a.TimeUs, a.Rf);
        rf.Color = Color.FromHex("#c3c2b7");
        rf.LineWidth = 1;
        rf.LegendText = "RF";
        var env = plt.Add.SignalXY(a.TimeUs, a.Envelope);
        env.Color = Theme.Ink2;
        env.LineWidth = 1.5f;
        env.LegendText = $"element {a.Element} pulse-echo envelope";
        foreach (var (gate, res, color, name) in new[] { (a.GateA, a.A, Theme.Series2, "A"), (a.GateB, a.B, Theme.Series1, "B") })
        {
            var gl = plt.Add.Line(gate.Start * 1e6, gate.Threshold, gate.End * 1e6, gate.Threshold);
            gl.Color = color;
            gl.LineWidth = 4;
            gl.LegendText = $"gate {name}";
            if (res.Triggered)
            {
                var m = plt.Add.Marker(res.Time * 1e6, res.Amplitude, MarkerShape.FilledCircle, 8);
                m.Color = color;
            }
        }
        plt.Axes.SetLimits(0, a.TimeUs[^1], -100, 110);
        plt.Title($"A-scan, element {a.Element} (back wall = 80 % FSH)");
        plt.XLabel("time, µs");
        plt.YLabel("% FSH");
        plt.ShowLegend(Alignment.LowerLeft);
        _ascan.Refresh();
    }

    /// <summary>A plot holding one persistent heatmap + colour bar, plus per-frame overlays.</summary>
    private sealed class ImageView
    {
        private readonly List<IPlottable> _overlays = [];
        private Heatmap? _heatmap;
        private ScottPlot.Panels.ColorBar? _colorBar;

        public ImageView(WpfPlot plot, IColormap colormap, string title)
        {
            Plot = plot;
            Colormap = colormap;
            Theme.Style(plot.Plot);
            plot.Plot.Title(title);
            plot.Refresh();
        }

        public WpfPlot Plot { get; }
        private IColormap Colormap { get; }

        public void Show(double[,] data, CoordinateRect extent, double range)
        {
            if (_heatmap is null)
            {
                _heatmap = Plot.Plot.Add.Heatmap(data);
                _heatmap.Colormap = Colormap;
                _heatmap.NaNCellColor = Colors.Transparent;
                _colorBar = Plot.Plot.Add.ColorBar(_heatmap);
                _colorBar.Label = "dB";
            }
            else
            {
                _heatmap.Intensities = data;
            }
            _heatmap.Extent = extent;
            _heatmap.ManualRange = new ScottPlot.Range(-range, 0);
            _heatmap.Update();
            _heatmap.IsVisible = true;
            _colorBar!.IsVisible = true;
        }

        public void Hide(string title)
        {
            if (_heatmap is not null) _heatmap.IsVisible = false;
            if (_colorBar is not null) _colorBar.IsVisible = false;
            Plot.Plot.Title(title);
            Plot.Refresh();
        }

        public void AddOverlay(IPlottable p) => _overlays.Add(p);

        public void ClearOverlays()
        {
            foreach (var p in _overlays) Plot.Plot.Remove(p);
            _overlays.Clear();
        }
    }
}
