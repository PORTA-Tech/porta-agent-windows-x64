using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

/// <summary>
/// TEMPORARY — a render harness for phase 4, not a test of behaviour. The repo has preview
/// harnesses for the report page and the Control Center pages but none for the widget, and phase
/// 4 is entirely widget work. Three builds of the Projects page were handed over unseen and the
/// problem was visible in the first ten seconds each time; this exists so that does not happen
/// again here. Writes PNGs to PORTA_WIDGET_SHOT_DIR and is skipped when that is unset.
/// </summary>
[Collection(WidgetSizeLogCollection.Name)]
public sealed class Phase4RenderHarness
{
    private static string? ShotDirectory => Environment.GetEnvironmentVariable("PORTA_WIDGET_SHOT_DIR");

    [Fact]
    public void RenderPhase4Screens()
    {
        string? outputDirectory = ShotDirectory;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return; // not a render run — this harness is opt-in
        }

        Directory.CreateDirectory(outputDirectory);

        RunOnStaThread(() =>
        {
            string placement = Path.Combine(outputDirectory, "widget-placement.json");
            var window = new UserDesktopWidgetWindow(_ => { }, _ => { }, new UserDesktopWidgetPlacementStore(placement));

            // ── The menu ───────────────────────────────────────────────────────────────────
            Popup popup = GetPrivateField<Popup>(window, "_menuPopup");
            var menu = (FrameworkElement)popup.Child;
            Save(menu, Path.Combine(outputDirectory, "01-menu.png"));

            // ── The task rows, with a title that overflows its 96 DIPs ──────────────────────
            window.ApplyState(new UserDesktopWidgetState(
                Mode: UserDesktopWidgetMode.Monitoring,
                Title: "Today",
                StatusText: "Tracking normally.",
                MessageSeverity: UserDesktopWidgetMessageSeverity.Info,
                FooterText: "Confidence High | Tracking quality strong",
                PrimaryMetric: new UserDesktopWidgetMetric("Work today", "5h 14m"),
                SecondaryMetric: new UserDesktopWidgetMetric("Last focus session", "28m"),
                TertiaryMetric: new UserDesktopWidgetMetric("Pressure", "31%"),
                CanPause: true,
                CanResume: false,
                Tasks:
                [
                    new UserDesktopWidgetTaskInfo(
                        1, "Rewrite the client onboarding sequence end to end", 7200, "active", 1800, 1500, 300, 0),
                    new UserDesktopWidgetTaskInfo(2, "Short one", 1800, "pending", 0, 0, 0, 0),
                    new UserDesktopWidgetTaskInfo(
                        3, "Finish the send button in the control center", 3600, "done", 3400, 3000, 400, 0)
                ]));

            var root = GetPrivateField<Border>(window, "_rootBorder");
            Save(root, Path.Combine(outputDirectory, "02-tasks-at-rest.png"));

            // Mid-scroll: drive the transform directly rather than waiting on the hover animation,
            // so the picture shows what the tail of a long title actually looks like once revealed.
            foreach (Canvas host in FindVisualChildren<Canvas>(root))
            {
                if (host.Children.Count == 1 && host.Children[0] is TextBlock scrolling
                    && scrolling.RenderTransform is TranslateTransform slide)
                {
                    double natural = Measure(scrolling.Text, scrolling);
                    scrolling.Width = natural;
                    scrolling.TextTrimming = TextTrimming.None;
                    slide.X = -(natural - host.Width);
                }
            }

            Save(root, Path.Combine(outputDirectory, "03-tasks-scrolled.png"));

            window.AllowCloseForShutdown();
            window.Close();
        });
    }

    private static double Measure(string text, TextBlock like)
    {
        var probe = new TextBlock { Text = text, FontSize = like.FontSize, FontFamily = like.FontFamily };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();

        int width = (int)Math.Ceiling(element.ActualWidth <= 0 ? element.DesiredSize.Width : element.ActualWidth);
        int height = (int)Math.Ceiling(element.ActualHeight <= 0 ? element.DesiredSize.Height : element.ActualHeight);
        Assert.True(width > 0 && height > 0, $"nothing to render for {Path.GetFileName(path)}");

        // 3x, then a flat backdrop: the widget is a small dark surface and a 1x shot of it is not
        // something anyone can judge a layout from.
        const int Scale = 3;
        var bitmap = new RenderTargetBitmap(width * Scale, height * Scale, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext dc = backdrop.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x15)), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(backdrop);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static T GetPrivateField<T>(object instance, string name)
    {
        System.Reflection.FieldInfo field = instance.GetType().GetField(
            name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"field {name} not found");
        return (T)field.GetValue(instance)!;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(30)), "Timed out rendering phase 4 screens.");
        thread.Join(TimeSpan.FromSeconds(10));

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
