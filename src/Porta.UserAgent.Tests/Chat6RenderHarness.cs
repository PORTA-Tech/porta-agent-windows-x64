using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

/// <summary>
/// Chat 6 (2026-08-24) — a render harness for the three widget fixes, same shape and same reasons
/// as <see cref="Phase4RenderHarness"/>: the widget is where all three live, and a fix handed over
/// unseen is how the Projects page got built three times.
///
/// Writes PNGs to PORTA_WIDGET_SHOT_DIR and is skipped when that is unset, so it costs the gate
/// nothing.
///
/// <b>What a still cannot show, and why.</b> Whether the result box actually TAKES THE KEYBOARD
/// depends on which process owns the foreground, which no unit test can assert — the codebase says
/// so itself where it suppresses foreground acquisition in the card focus tests. The picture shows
/// the box open and armed; the keystrokes are confirmed by installing. Likewise the dwell is a
/// duration: what is photographed here is the surface it falls back to during the gap, which is the
/// thing that was missing.
/// </summary>
[Collection(WidgetSizeLogCollection.Name)]
public sealed class Chat6RenderHarness
{
    private static string? ShotDirectory => Environment.GetEnvironmentVariable("PORTA_WIDGET_SHOT_DIR");

    [Fact]
    public void RenderChat6Screens()
    {
        string? outputDirectory = ShotDirectory;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return; // not a render run — opt-in, like the phase 4 harness
        }

        Directory.CreateDirectory(outputDirectory);

        RunOnStaThread(() =>
        {
            string placement = Path.Combine(outputDirectory, "chat6-placement.json");
            var window = new UserDesktopWidgetWindow(_ => { }, _ => { }, new UserDesktopWidgetPlacementStore(placement));

            window.ApplyState(TaskState());
            var root = GetPrivateField<Border>(window, "_rootBorder");

            // ── A. the result prompt, open ────────────────────────────────────────────────
            // "What came of it?" could not be typed into: the box was focused inside a window that
            // is never foreground. It now runs the same activation sequence the other two boxes do.
            Invoke(window, "OpenResultPrompt", 3L, "Finish the send button in the control center");
            Save(root, Path.Combine(outputDirectory, "10-result-prompt-open.png"));

            // With an answer typed, to show the box takes text and the controls sit right under it.
            GetPrivateField<TextBox>(window, "_resultBox").Text =
                "Sent to the client for sign-off, with the new pricing section.";
            Save(root, Path.Combine(outputDirectory, "11-result-prompt-answered.png"));

            Invoke(window, "CloseResultPrompt");

            // ── B. the dwell ──────────────────────────────────────────────────────────────
            // A card on the inline surface...
            window.ApplyState(TaskState(withAsk: true));
            Save(root, Path.Combine(outputDirectory, "12-card-on-the-surface.png"));

            // ...answered, and the surface held. This is the gap that did not exist: the widget
            // goes back to being a widget before the next card may take the surface.
            Invoke(window, "HoldInlineSurfaceAfterResolve");
            Invoke(window, "RefreshInlineMessage");
            Save(root, Path.Combine(outputDirectory, "13-surface-held-after-resolve.png"));

            window.AllowCloseForShutdown();
            window.Close();
        });
    }

    private static UserDesktopWidgetState TaskState(bool withAsk = false) => new(
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
            new UserDesktopWidgetTaskInfo(3, "Finish the send button in the control center", 3600, "done", 3400, 3000, 400, 0),
            new UserDesktopWidgetTaskInfo(1, "Landing page copy", 7200, "active", 1800, 1500, 300, 0)
        ],
        TaskAsk: withAsk
            ? new UserDesktopWidgetTaskAsk(
                "ask-1", 1, "Landing page copy", 7200,
                "“Landing page copy” has had the 2h you gave it.")
            : null);

    private static void Invoke(object target, string method, params object[] args)
    {
        MethodInfo info = target.GetType().GetMethod(
            method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{method} not found");
        info.Invoke(target, args);
    }

    private static T GetPrivateField<T>(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{name} not found");
        return (T)field.GetValue(target)!;
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();

        int width = (int)Math.Ceiling(element.ActualWidth <= 0 ? element.DesiredSize.Width : element.ActualWidth);
        int height = (int)Math.Ceiling(element.ActualHeight <= 0 ? element.DesiredSize.Height : element.ActualHeight);
        Assert.True(width > 0 && height > 0, $"nothing to render for {Path.GetFileName(path)}");

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

        Assert.True(completed.Wait(TimeSpan.FromSeconds(60)), "Timed out rendering the chat 6 screens.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Timed out joining the chat 6 render thread.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
