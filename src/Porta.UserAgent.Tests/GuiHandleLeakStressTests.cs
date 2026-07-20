using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

/// <summary>
/// Fleet §1d — the ship gate for the USER-handle leak (PEACH20/AYMENE). Every window surface the
/// UserAgent creates and recreates (desktop widget, break overlay, check-in card) is cycled
/// open→close many times on one live dispatcher — the exact shape of the production recreate
/// loop — and the process USER/GDI handle counts must return to baseline. Before the §1b
/// disposal fix (six un-stopped DispatcherTimers rooting every dead widget window) this climbs
/// monotonically; after it, the counts stay flat.
/// </summary>
public sealed class GuiHandleLeakStressTests : IDisposable
{
    private const int WarmupCycles = 5;
    private const int StressCycles = 60;
    // Small drift allowance for WPF's lazily-created shared resources (fonts, input managers).
    private const uint MaxUserHandleGrowth = 40;
    private const uint MaxGdiHandleGrowth = 60;

    private readonly string _sizeLogRedirect =
        Path.Combine(Path.GetTempPath(), $"widget-size-stress-{Guid.NewGuid():N}.log");

    public GuiHandleLeakStressTests()
    {
        WidgetSizeLog.PathOverride = _sizeLogRedirect;
    }

    public void Dispose()
    {
        WidgetSizeLog.PathOverride = null;
        try
        {
            if (File.Exists(_sizeLogRedirect))
            {
                File.Delete(_sizeLogRedirect);
            }
        }
        catch
        {
        }
    }

    // One fact, one STA thread, one process-wide baseline per surface, measured sequentially —
    // separate facts on fresh threads proved flaky because GetGuiResources is process-wide and
    // a prior fact's dead-thread cleanup releases handles asynchronously under the next fact's
    // measurement. This sequential shape reproduced stable, exactly-flat counts.
    [Fact]
    public void AllInterventionSurfaces_OpenCloseCycles_ReturnToHandleBaseline()
    {
        RunOnStaThread(() =>
        {
            using TempDirectoryScope tempDirectory = new();
            var placementStore = new UserDesktopWidgetPlacementStore(
                Path.Combine(tempDirectory.Path, "widget-placement.json"));

            AssertNoHandleClimb("widget", () =>
            {
                var window = new UserDesktopWidgetWindow(_ => { }, _ => { }, placementStore);
                window.StartHidden(); // real HWND, no visible flash
                window.AllowCloseForShutdown();
                window.Close();
            });

            AssertNoHandleClimb("checkin_card", () =>
            {
                var window = new UserCheckInCardWindow(_ => { });
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                window.Close();
            });

            AssertNoHandleClimb("break_overlay", () =>
            {
                var window = new UserBreakOverlayWindow(_ => { });
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                // The overlay (like the widget) cancels Close unless shutdown is authorized —
                // a plain Close() leaves a LIVE window and its HWND behind, which is precisely
                // the accumulation mode that exhausted USER handles in the field. Every
                // production teardown path must go through this authorized close.
                window.AllowCloseForShutdown();
                window.Close();
            });
        });
    }

    // The production failure mode (PEACH20/AYMENE): a window that is REPLACED without being
    // closed keeps its HWND and timers alive on the dispatcher — each recreate stacks another.
    // This pins the meter itself: un-closed windows must register as a climb, so the green
    // assertions above are meaningful.
    [Fact]
    public void OrphanedWindows_WithoutClose_AreDetectedByTheMeter()
    {
        RunOnStaThread(() =>
        {
            var keepAlive = new List<UserBreakOverlayWindow>();
            for (int i = 0; i < 10; i++)
            {
                var window = new UserBreakOverlayWindow(_ => { });
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                keepAlive.Add(window);
            }

            (uint before, _) = GuiResourceMeter.Read();

            for (int i = 0; i < 20; i++)
            {
                var window = new UserBreakOverlayWindow(_ => { });
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                keepAlive.Add(window); // never closed — the orphan mode
            }

            (uint after, _) = GuiResourceMeter.Read();
            Assert.True(
                after >= before + 20,
                $"Expected >= 20 USER handles from 20 live windows, saw {before} -> {after} — the meter would miss the field leak.");

            foreach (UserBreakOverlayWindow window in keepAlive)
            {
                window.AllowCloseForShutdown();
                window.Close();
            }
        });
    }

    private static void AssertNoHandleClimb(string surface, Action openCloseCycle)
    {
        // Warm-up pays WPF's one-time lazy costs (theme dictionaries, fonts, input managers)
        // so the measured window is purely the per-cycle delta.
        for (int i = 0; i < WarmupCycles; i++)
        {
            openCloseCycle();
        }

        DrainDispatcherAndGc();
        (uint baselineUser, uint baselineGdi) = GuiResourceMeter.Read();
        Assert.True(baselineUser > 0, "GetGuiResources returned 0 — instrumentation broken.");

        for (int i = 0; i < StressCycles; i++)
        {
            openCloseCycle();
        }

        DrainDispatcherAndGc();
        (uint finalUser, uint finalGdi) = GuiResourceMeter.Read();

        Assert.True(
            finalUser <= baselineUser + MaxUserHandleGrowth,
            $"{surface}: USER handles climbed {baselineUser} -> {finalUser} over {StressCycles} open/close cycles — window teardown is leaking.");
        Assert.True(
            finalGdi <= baselineGdi + MaxGdiHandleGrowth,
            $"{surface}: GDI handles climbed {baselineGdi} -> {finalGdi} over {StressCycles} open/close cycles — window teardown is leaking.");
    }

    private static void DrainDispatcherAndGc()
    {
        // Let queued dispatcher work (Closed handlers, render/input cleanup) finish, then force
        // finalization so dead windows release their native resources before we measure.
        for (int i = 0; i < 3; i++)
        {
            PushDispatcherFrame();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        PushDispatcherFrame();
    }

    private static void PushDispatcherFrame()
    {
        // SystemIdle is the LOWEST priority — pumping until it runs guarantees every queued
        // cleanup item (WPF defers some teardown below ContextIdle) has executed first.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public TempDirectoryScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"porta-handle-stress-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
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
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // Generous: the widget window builds a large visual tree per cycle.
        Assert.True(completed.Wait(TimeSpan.FromSeconds(120)), "Timed out waiting for the handle stress test to finish.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for the STA stress thread to join.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
