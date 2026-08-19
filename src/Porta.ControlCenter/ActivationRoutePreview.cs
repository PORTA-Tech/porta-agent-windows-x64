using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Porta.ControlCenter;

/// <summary>
/// 2026-08-19 — proof that a page opened from the WIDGET has content and not just a frame.
///
/// <para><see cref="ReportsPagePreview"/> cannot answer this. It runs with
/// <c>MainWindow.PreviewMode = true</c> and hands the pages their rows itself, which is right for
/// judging a design and useless for this: the bug was that the real load never ran, and a harness
/// that fills the page by hand renders a perfect picture of a broken build.</para>
///
/// <para>So this one runs with PreviewMode OFF, against the live local API, and reaches each page
/// through <see cref="MainWindow.HandleExternalActivation"/> — the exact entry point the widget
/// menu uses. What it photographs is what the widget opens.</para>
///
/// <list type="bullet">
/// <item><c>--preview-activation</c> — writes one PNG per page and prints the folder.</item>
/// </list>
/// </summary>
internal static class ActivationRoutePreview
{
    internal const string Flag = "--preview-activation";

    internal static bool ShouldRun(string[] args)
        => args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    internal static void Run(string[] args)
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "porta-activation-preview",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // NOT PreviewMode. The whole question is whether the ordinary load runs.
        MainWindow.PreviewMode = false;

        (string Name, ControlCenterActivationTarget Target)[] pages =
        [
            ("tasks", ControlCenterActivationTarget.Tasks),
            ("projects", ControlCenterActivationTarget.Projects),
            ("reports", ControlCenterActivationTarget.Reports),
            ("history", ControlCenterActivationTarget.History),
            ("settings", ControlCenterActivationTarget.Settings),
            ("account", ControlCenterActivationTarget.Account),
            ("today", ControlCenterActivationTarget.Now),
            ("system", ControlCenterActivationTarget.Health)
        ];

        // ONE window for every page, deliberately. Closing a MainWindow disposes the shared local
        // API client — the same seam ReportsPagePreview notes — so a fresh window per page leaves
        // every page after the first with a dead client and the process dies on the second one.
        // Reusing it also happens to be the commoner case in real life: the Control Center is
        // already open and the widget asks it for a page (App.OnStartup's running-instance path).
        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = 1280,
            Height = 900,
            ShowInTaskbar = false,
            WindowState = WindowState.Normal
        };
        window.Show();

        int written = 0;
        foreach ((string name, ControlCenterActivationTarget target) in pages)
        {
            // The widget's path, verbatim.
            window.HandleExternalActivation(target);

            // HandleExternalActivation ends by activating and focusing, which pulls the window
            // onto the screen and in front of whatever the user is doing. Put it back off-screen
            // before waiting — the load has already been started by this point.
            window.Left = -20000;
            window.Top = -20000;

            // The loads are async against the local API. Pump while they land.
            PumpFor(TimeSpan.FromSeconds(6));

            string file = Path.Combine(dir, $"{++written:00}-{name}.png");
            SavePng((FrameworkElement)window.Content, file);
            Console.WriteLine($"{Path.GetFileName(file)}");
        }

        Console.WriteLine(dir);
        Application.Current.Shutdown();
    }

    private static void PumpFor(TimeSpan duration)
    {
        DateTime deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(25);
        }
    }

    private static void SavePng(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        int width = (int)Math.Ceiling(element.ActualWidth);
        int height = (int)Math.Ceiling(element.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            Console.WriteLine($"  nothing to render for {Path.GetFileName(path)}");
            return;
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
