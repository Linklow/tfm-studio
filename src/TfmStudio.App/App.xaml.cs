using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TfmStudio.App.ViewModels;

namespace TfmStudio.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        MainWindow = window;

        int s = Array.IndexOf(e.Args, "--screenshot");
        if (s >= 0 && s + 1 < e.Args.Length)
        {
            // Headless capture for the README: run part of the scan, then render the window to PNG.
            int f = Array.IndexOf(e.Args, "--frames");
            int frames = f >= 0 && f + 1 < e.Args.Length ? int.Parse(e.Args[f + 1], CultureInfo.InvariantCulture) : 161;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.ShowActivated = false;
            window.Show();
            await vm.RunFramesAsync(frames);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveScreenshot(window, e.Args[s + 1]);
            Shutdown();
            return;
        }
        window.Show();
    }

    private static void SaveScreenshot(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        int w = (int)root.ActualWidth, h = (int)root.ActualHeight;
        // Compose on an opaque page colour: transparent pixels turn black in dark-mode viewers (GitHub dark theme).
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)window.FindResource("PageBrush"), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
