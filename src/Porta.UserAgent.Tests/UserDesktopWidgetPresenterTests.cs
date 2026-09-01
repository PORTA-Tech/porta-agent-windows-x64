using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserDesktopWidgetPresenterTests
{
    /// <summary>
    /// How long to allow for creating a real WPF window on a fresh STA thread.
    ///
    /// This was 5 seconds and it made the suite flaky. The FIRST window in a process pays for
    /// framework and theme-resource initialisation, which is instant on a warm developer machine
    /// and can take far longer on a cold, shared CI runner. The result was
    /// "Timed out waiting for widget window creation" on main on 2026-08-19 and again on
    /// 2026-08-24, passing on the very next run with a superset of the same code — twice sending
    /// someone to look for a regression that was never there.
    ///
    /// A generous ceiling costs nothing when the window appears promptly, which is every healthy
    /// run: the wait returns as soon as it is signalled. It only changes how long a genuinely
    /// hung creation takes to be reported, and a hang still fails. The assertion is unchanged —
    /// this is a deadline for a slow machine, never a weaker check.
    /// </summary>
    private static readonly TimeSpan WindowCreationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The same reasoning for tearing a widget thread down and for the cross-thread worker: a
    /// loaded runner is slow at both ends, not only at startup.
    /// </summary>
    private static readonly TimeSpan ThreadSettleTimeout = TimeSpan.FromSeconds(30);

    // ── Phase 4.2 — "Show when it returns" ─────────────────────────────────────────────────
    // The hide is only half the feature; the user has to be told when the widget comes back,
    // in clock time, or a timed hide is indistinguishable from the widget having crashed.

    /// <summary>
    /// A wall-clock local time, whatever zone the machine or CI runner is in. The store hands the
    /// presenter values already carrying the local offset, so this matches what production passes
    /// — and the assertions below stay true in Dubai and on a UTC build agent alike.
    /// </summary>
    private static DateTimeOffset LocalAt(int day, int hour, int minute)
    {
        DateTime wall = new(2026, 08, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }

    [Fact]
    public void HiddenUntilMessage_NamesTheClockTime()
    {
        Assert.Equal(
            "Hidden until 9:15 PM.",
            UserDesktopWidgetPresenter.BuildHiddenUntilMessage(LocalAt(18, 21, 15), string.Empty));
    }

    [Fact]
    public void HiddenUntilMessage_DropsLeadingZeroAndKeepsMinutes()
    {
        Assert.Equal(
            "Hidden until 5:00 AM.",
            UserDesktopWidgetPresenter.BuildHiddenUntilMessage(LocalAt(19, 5, 0), string.Empty));
    }

    [Fact]
    public void HiddenUntilMessage_AppendsTheSettingsTrailerForTheDailyHide()
    {
        Assert.Equal(
            "Hidden until 5:00 AM. Turn it off for good in Settings.",
            UserDesktopWidgetPresenter.BuildHiddenUntilMessage(
                LocalAt(19, 5, 0), "Turn it off for good in Settings."));
    }

    [Fact]
    public void HiddenUntilMessage_WithNoReturnTime_DoesNotInventOne()
    {
        // Permanently disabled, or a store read that failed. Saying nothing about the return is
        // honest; naming a time the widget will not honour is not.
        Assert.Equal("Widget hidden.", UserDesktopWidgetPresenter.BuildHiddenUntilMessage(null, string.Empty));
        Assert.Equal(
            "Widget hidden.",
            UserDesktopWidgetPresenter.BuildHiddenUntilMessage(DateTimeOffset.MaxValue, string.Empty));
    }

    [Fact]
    public async Task HeartbeatHealthCheck_TreatsCrossThreadWindowAsUnhealthyWithoutThrowing()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-widget-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        UserDesktopWidgetPresenter? presenter = null;
        Thread? widgetOwnerThread = null;
        var created = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        Exception? widgetCreationFailure = null;
        UserDesktopWidgetWindow? foreignThreadWindow = null;

        try
        {
            var breakOverlayPresenter = new FakeBreakOverlayPresenter();
            presenter = new UserDesktopWidgetPresenter(
                new UserDesktopWidgetRollout(WidgetEnabled: true, ForceToastFallback: false),
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory, "widget-placement.json")),
                new UserDesktopWidgetVisibilityStore(Path.Combine(tempDirectory, "widget-hidden.flag")),
                breakOverlayPresenter,
                startWidgetThread: false);

            string placementPath = Path.Combine(tempDirectory, "widget-owner-placement.json");
            widgetOwnerThread = new Thread(() =>
            {
                try
                {
                    foreignThreadWindow = new UserDesktopWidgetWindow(
                        _ => { },
                        _ => { },
                        new UserDesktopWidgetPlacementStore(placementPath));
                }
                catch (Exception ex)
                {
                    widgetCreationFailure = ex;
                }
                finally
                {
                    created.Set();
                }

                release.Wait();
            });

            widgetOwnerThread.SetApartmentState(ApartmentState.STA);
            widgetOwnerThread.IsBackground = true;
            widgetOwnerThread.Start();

            Assert.True(created.Wait(WindowCreationTimeout), "Timed out waiting for widget window creation.");
            Assert.Null(widgetCreationFailure);
            Assert.NotNull(foreignThreadWindow);

            SetPrivateField(presenter, "_widgetWindow", foreignThreadWindow);
            SetPrivateField(presenter, "_widgetDispatcher", Dispatcher.CurrentDispatcher);
            SetPrivateField(presenter, "_widgetThread", Thread.CurrentThread);
            SetPrivateField(presenter, "_widgetHealthState", 2);
            SetPrivateField(foreignThreadWindow, "_surfaceVisibleForHeartbeat", 1);

            Exception? heartbeatFailure = null;
            bool heartbeatHealthy = false;

            Task worker = Task.Run(() =>
            {
                try
                {
                    heartbeatHealthy = presenter.IsWidgetHealthyForSessionHeartbeat;
                }
                catch (Exception ex)
                {
                    heartbeatFailure = ex;
                }
            });

            await worker.WaitAsync(ThreadSettleTimeout);

            Assert.Null(heartbeatFailure);
            Assert.False(heartbeatHealthy);
        }
        finally
        {
            presenter?.Dispose();
            release.Set();

            if (widgetOwnerThread is { IsAlive: true })
            {
                Assert.True(widgetOwnerThread.Join(ThreadSettleTimeout), "Timed out waiting for widget owner thread shutdown.");
            }

            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void HeartbeatHealthCheck_RequiresVisibleSurfaceState()
    {
        RunOnStaThread(() =>
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-widget-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            var breakOverlayPresenter = new FakeBreakOverlayPresenter();
            var presenter = new UserDesktopWidgetPresenter(
                new UserDesktopWidgetRollout(WidgetEnabled: true, ForceToastFallback: false),
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory, "widget-placement.json")),
                new UserDesktopWidgetVisibilityStore(Path.Combine(tempDirectory, "widget-hidden.flag")),
                breakOverlayPresenter,
                startWidgetThread: false);

            UserDesktopWidgetWindow? window = null;
            try
            {
                window = new UserDesktopWidgetWindow(
                    _ => { },
                    _ => { },
                    new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory, "widget-window-placement.json")));

                SetPrivateField(presenter, "_widgetDispatcher", Dispatcher.CurrentDispatcher);
                SetPrivateField(presenter, "_widgetWindow", window);
                SetPrivateField(presenter, "_widgetHealthState", 2);
                SetPrivateField(window, "_surfaceVisibleForHeartbeat", 0);

                Assert.False(presenter.IsWidgetHealthyForSessionHeartbeat);
            }
            finally
            {
                CloseWindow(window);
                SetPrivateField<Thread?>(presenter, "_widgetThread", null);
                SetPrivateField<Dispatcher?>(presenter, "_widgetDispatcher", null);
                SetPrivateField<UserDesktopWidgetWindow?>(presenter, "_widgetWindow", null);
                presenter.Dispose();
                Directory.Delete(tempDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void Recovery_RecreatesClosingWindowWithoutThrowing()
    {
        RunOnStaThread(() =>
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-widget-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            var breakOverlayPresenter = new FakeBreakOverlayPresenter();
            var presenter = new UserDesktopWidgetPresenter(
                new UserDesktopWidgetRollout(WidgetEnabled: true, ForceToastFallback: false),
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory, "widget-placement.json")),
                new UserDesktopWidgetVisibilityStore(Path.Combine(tempDirectory, "widget-hidden.flag")),
                breakOverlayPresenter,
                startWidgetThread: false);

            UserDesktopWidgetWindow? originalWindow = null;
            UserDesktopWidgetWindow? recoveredWindow = null;

            try
            {
                originalWindow = new UserDesktopWidgetWindow(
                    _ => { },
                    _ => { },
                    new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory, "widget-original-placement.json")));
                originalWindow.Show();
                SetPrivateField(originalWindow, "_windowLifecycleState", 1);

                SetPrivateField(presenter, "_widgetDispatcher", Dispatcher.CurrentDispatcher);
                SetPrivateField(presenter, "_widgetWindow", originalWindow);

                Exception? recoveryFailure = Record.Exception(() => InvokePrivateMethod(presenter, "EnsureWidgetWindowOnCurrentDispatcher"));
                Assert.Null(recoveryFailure);

                recoveredWindow = GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow");
                Assert.NotNull(recoveredWindow);
                Assert.NotSame(originalWindow, recoveredWindow);
                Assert.True(recoveredWindow.CanAcceptUiActions);
            }
            finally
            {
                CloseWindow(recoveredWindow);
                if (!ReferenceEquals(originalWindow, recoveredWindow))
                {
                    CloseWindow(originalWindow);
                }

                SetPrivateField<Thread?>(presenter, "_widgetThread", null);
                SetPrivateField<Dispatcher?>(presenter, "_widgetDispatcher", null);
                SetPrivateField<UserDesktopWidgetWindow?>(presenter, "_widgetWindow", null);
                presenter.Dispose();
                Directory.Delete(tempDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void Recovery_RecreatesForeignDispatcherWindowWithoutThrowing()
    {
        RunOnStaThread(() =>
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "porta-widget-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            UserDesktopWidgetPresenter? presenter = null;
            Thread? widgetOwnerThread = null;
            var created = new ManualResetEventSlim(false);
            Exception? widgetCreationFailure = null;
            UserDesktopWidgetWindow? foreignThreadWindow = null;

            try
            {
                var breakOverlayPresenter = new FakeBreakOverlayPresenter();
                presenter = new UserDesktopWidgetPresenter(
                    new UserDesktopWidgetRollout(WidgetEnabled: true, ForceToastFallback: false),
                    new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory, "widget-placement.json")),
                    new UserDesktopWidgetVisibilityStore(Path.Combine(tempDirectory, "widget-hidden.flag")),
                    breakOverlayPresenter,
                    startWidgetThread: false);

                string placementPath = Path.Combine(tempDirectory, "widget-owner-placement.json");
                widgetOwnerThread = new Thread(() =>
                {
                    try
                    {
                        foreignThreadWindow = new UserDesktopWidgetWindow(
                            _ => { },
                            _ => { },
                            new UserDesktopWidgetPlacementStore(placementPath));
                        foreignThreadWindow.Show();
                    }
                    catch (Exception ex)
                    {
                        widgetCreationFailure = ex;
                    }
                    finally
                    {
                        created.Set();
                    }

                    Dispatcher.Run();
                });

                widgetOwnerThread.SetApartmentState(ApartmentState.STA);
                widgetOwnerThread.IsBackground = true;
                widgetOwnerThread.Start();

                Assert.True(created.Wait(WindowCreationTimeout), "Timed out waiting for foreign widget window creation.");
                Assert.Null(widgetCreationFailure);
                Assert.NotNull(foreignThreadWindow);

                SetPrivateField(presenter!, "_widgetDispatcher", Dispatcher.CurrentDispatcher);
                SetPrivateField(presenter!, "_widgetWindow", foreignThreadWindow);

                Exception? recoveryFailure = Record.Exception(() => InvokePrivateMethod(presenter!, "EnsureWidgetWindowOnCurrentDispatcher"));
                Assert.Null(recoveryFailure);

                UserDesktopWidgetWindow recoveredWindow = GetPrivateField<UserDesktopWidgetWindow>(presenter!, "_widgetWindow");
                Assert.NotNull(recoveredWindow);
                Assert.NotSame(foreignThreadWindow, recoveredWindow);
                Assert.True(ReferenceEquals(recoveredWindow.Dispatcher, Dispatcher.CurrentDispatcher));
            }
            finally
            {
                UserDesktopWidgetWindow? recoveredWindow = null;
                if (presenter != null)
                {
                    try
                    {
                        recoveredWindow = GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow");
                    }
                    catch
                    {
                    }

                    CloseWindow(recoveredWindow);
                    SetPrivateField<Thread?>(presenter, "_widgetThread", null);
                    SetPrivateField<Dispatcher?>(presenter, "_widgetDispatcher", null);
                    SetPrivateField<UserDesktopWidgetWindow?>(presenter, "_widgetWindow", null);
                    presenter.Dispose();
                }

                if (foreignThreadWindow != null && widgetOwnerThread is { IsAlive: true })
                {
                    try
                    {
                        foreignThreadWindow.Dispatcher.Invoke(() =>
                        {
                            if (!foreignThreadWindow.IsClosingOrClosed)
                            {
                                foreignThreadWindow.AllowCloseForShutdown();
                            }

                            if (foreignThreadWindow.IsLoaded)
                            {
                                foreignThreadWindow.Close();
                            }

                            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                        });
                    }
                    catch
                    {
                    }
                }

                if (widgetOwnerThread is { IsAlive: true })
                {
                    Assert.True(widgetOwnerThread.Join(ThreadSettleTimeout), "Timed out waiting for foreign widget owner thread shutdown.");
                }

                Directory.Delete(tempDirectory, recursive: true);
            }
        });
    }

    [Fact]
    public void TryPresentPrompt_BreakPrompt_UsesDedicatedBreakOverlayEvenWhenWidgetIsDisabled()
    {
        var breakOverlayPresenter = new FakeBreakOverlayPresenter { ShowResult = true };
        using var presenter = new UserDesktopWidgetPresenter(
            new UserDesktopWidgetRollout(WidgetEnabled: false, ForceToastFallback: true),
            new UserDesktopWidgetPlacementStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "widget-placement.json")),
            new UserDesktopWidgetVisibilityStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "widget-hidden.flag")),
            breakOverlayPresenter,
            startWidgetThread: false);

        bool shown = presenter.TryPresentPrompt(
            new UserInterventionPrompt(
                InterventionId: "break-1",
                TipKey: "break_suggestion",   // legacy name still routes to overlay
                Title: "Long break",
                Message: "Ready for a long break?",
                AcceptLabel: "Take break",
                DismissLabel: "Skip",
                SnoozeLabel: "Snooze",
                SuppressLabel: "Turn off this tip",
                DurationSeconds: 300),
            _ => { },
            out string errorCode);

        Assert.True(shown);
        Assert.Equal(string.Empty, errorCode);
        Assert.Equal(1, breakOverlayPresenter.PromptCalls);
    }

    [Theory]
    [InlineData("short_break")]
    [InlineData("long_break")]
    [InlineData("stretch_break_suggestion")]
    [InlineData("break_suggestion")]
    public void TryPresentPrompt_AnyBreakKind_AlwaysRoutesToOverlayNeverToWidget(string tipKey)
    {
        var breakOverlayPresenter = new FakeBreakOverlayPresenter { ShowResult = true };
        using var presenter = new UserDesktopWidgetPresenter(
            new UserDesktopWidgetRollout(WidgetEnabled: true, ForceToastFallback: false),
            new UserDesktopWidgetPlacementStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "widget-placement.json")),
            new UserDesktopWidgetVisibilityStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "widget-hidden.flag")),
            breakOverlayPresenter,
            startWidgetThread: false);

        bool shown = presenter.TryPresentPrompt(
            new UserInterventionPrompt(
                InterventionId: $"break-{tipKey}",
                TipKey: tipKey,
                Title: "Break time",
                Message: "Time for a break.",
                AcceptLabel: "Take break",
                DismissLabel: "Skip",
                SnoozeLabel: "Snooze",
                SuppressLabel: string.Empty,
                DurationSeconds: 120),
            _ => { },
            out string errorCode);

        // The widget surface must never receive break prompts regardless of kind.
        Assert.True(shown);
        Assert.Equal(string.Empty, errorCode);
        Assert.Equal(1, breakOverlayPresenter.PromptCalls);
    }

    private static void SetPrivateField<TValue>(object target, string fieldName, TValue value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field '{fieldName}'.");
        field.SetValue(target, value);
    }

    // ── The hidden-widget rebuild loop ─────────────────────────────────────────────────────
    //
    // Two independent faults made a hidden widget rebuild itself ~20 times a minute, leaking a
    // window's worth of USER and GDI objects each time until the process hit the 10,000 handle
    // ceiling and the widget stopped working at all.
    //
    //  1. IsWidgetOperationalLocked required the SURFACE TO BE VISIBLE, so a deliberately hidden
    //     widget was never "operational". Every dispatch fell through to EnsureWidgetThreadStarted,
    //     which started a FRESH widget thread; the new thread's dispatcher then orphaned the
    //     existing window as a mismatch and rebuilt it.
    //  2. StartHidden() creates the HWND without calling Show(), so WPF never raises Loaded. The
    //     recovery path used IsLoaded alone to spot a leftover whose Show() had thrown, which also
    //     matched every deliberately hidden window and destroyed it.
    //
    // THE FIRST OF THESE IS WHAT PRODUCTION HITS, and the first version of these tests missed it
    // entirely: they set _widgetDispatcher by hand and called EnsureWidgetWindowOnCurrentDispatcher
    // directly, which is the stable-dispatcher path a hidden widget never reaches. They passed
    // against a build that still leaked 63 handles a minute on a real machine.
    //
    // So the headline tests below go through TryUpdateWidgetState — the same entry point the
    // capture loop uses — and let the presenter own its widget thread, exactly as in production.

    /// <summary>
    /// The production path. Widget hidden, many state pushes: ONE widget thread and ONE window.
    /// A new thread per push is the fault that leaked, and it is what this asserts against.
    /// </summary>
    [Fact]
    public void HiddenWidget_ManyStatePushes_ReuseOneThreadAndOneWindow()
    {
        RunHiddenProductionScenario(TimeSpan.FromHours(1), pushes: 20, (threads, windows) =>
        {
            Assert.Single(threads.Distinct());
            Assert.Single(windows.Distinct());
            Assert.True(
                OnWindow(windows[0], static w => w.StartedHidden),
                "a widget brought up hidden must be marked as such");
            Assert.False(
                OnWindow(windows[0], static w => w.IsLoaded),
                "and it is still not Loaded — that is what broke the reuse test");
        });
    }

    /// <summary>A hide that outlives a restart is the same shape in the store's other mode.</summary>
    [Fact]
    public void AHideThatOutlivesARestart_AlsoReusesOneThreadAndOneWindow()
    {
        RunHiddenProductionScenario(TimeSpan.FromHours(20), pushes: 20, (threads, windows) =>
        {
            Assert.Single(threads.Distinct());
            Assert.Single(windows.Distinct());
        });
    }

    /// <summary>
    /// The same run measured the way the field evidence was: handles flat, not climbing. Baseline
    /// is taken after the first push so the one legitimate window is not counted as growth.
    /// </summary>
    [Fact]
    public void HiddenWidget_DoesNotLeakHandlesAcrossStatePushes()
    {
        string dir = NewTempDirectory();
        string hidePath = Path.Combine(dir, "widget-hidden.flag");
        new UserDesktopWidgetVisibilityStore(hidePath).HideFor(TimeSpan.FromHours(1), DateTimeOffset.Now);

        UserDesktopWidgetPresenter? presenter = null;
        try
        {
            presenter = BuildPresenter(dir, hidePath);

            presenter.TryUpdateWidgetState(BuildSampleWidgetState(), out _);
            (uint baselineUser, uint baselineGdi) = GuiResourceMeter.Read();

            const int Pushes = 20;
            for (int i = 0; i < Pushes; i++)
            {
                presenter.TryUpdateWidgetState(BuildSampleWidgetState(), out _);
            }

            (uint finalUser, uint finalGdi) = GuiResourceMeter.Read();
            long userGrowth = (long)finalUser - baselineUser;
            long gdiGrowth = (long)finalGdi - baselineGdi;

            // Measured on a real machine before the fix: ~3 USER and ~3 GDI per rebuild. Across 20
            // pushes that is ~60 of each. The allowance absorbs unrelated churn on a shared runner
            // while leaving no room for a rebuild loop to hide.
            Assert.True(
                userGrowth <= 12,
                $"USER objects grew by {userGrowth} across {Pushes} pushes — the widget is being rebuilt.");
            Assert.True(
                gdiGrowth <= 12,
                $"GDI objects grew by {gdiGrowth} across {Pushes} pushes — the widget is being rebuilt.");
        }
        finally
        {
            DisposeQuietly(presenter);
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// A hidden widget must report itself operational, which is the condition that stops the
    /// dispatch path starting a fresh thread. Asserted directly so a regression names itself.
    /// </summary>
    [Fact]
    public void AHiddenWidget_CountsAsOperational()
    {
        string dir = NewTempDirectory();
        string hidePath = Path.Combine(dir, "widget-hidden.flag");
        new UserDesktopWidgetVisibilityStore(hidePath).HideFor(TimeSpan.FromHours(1), DateTimeOffset.Now);

        UserDesktopWidgetPresenter? presenter = null;
        try
        {
            presenter = BuildPresenter(dir, hidePath);
            presenter.TryUpdateWidgetState(BuildSampleWidgetState(), out _);

            UserDesktopWidgetWindow window =
                GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow");
            Assert.False(
                OnWindow(window, static w => w.IsSurfaceVisibleForHeartbeat),
                "it is hidden, so it is not on screen");
            Assert.True(
                OnWindow(window, static w => w.IsSurfaceHiddenOnPurpose),
                "but it is hidden on purpose, not broken");

            // And the heartbeat is unaffected: it short-circuits to healthy while hidden, which is
            // why widening the operational gate cannot change what the watchdog restarts on.
            Assert.True(presenter.IsWidgetHealthyForSessionHeartbeat);
        }
        finally
        {
            DisposeQuietly(presenter);
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// The recovery path, which is where the StartedHidden flag earns its keep. Recovery still runs
    /// on a genuine ready-timeout, and when it does it must reuse a hidden window rather than
    /// destroy it as a never-loaded leftover.
    /// </summary>
    [Fact]
    public void RecoveryOnTheExistingDispatcher_ReusesAHiddenWindow_RatherThanRebuildingIt()
    {
        string dir = NewTempDirectory();
        string hidePath = Path.Combine(dir, "widget-hidden.flag");
        new UserDesktopWidgetVisibilityStore(hidePath).HideFor(TimeSpan.FromHours(1), DateTimeOffset.Now);

        RunOnStaThread(() =>
        {
            UserDesktopWidgetPresenter? presenter = null;
            var seen = new List<UserDesktopWidgetWindow>();
            try
            {
                presenter = BuildPresenter(dir, hidePath);
                SetPrivateField(presenter, "_widgetDispatcher", Dispatcher.CurrentDispatcher);

                for (int i = 0; i < 10; i++)
                {
                    SetPrivateField(presenter, "_widgetReady", CreateReadySource());
                    InvokePrivateMethod(presenter, "EnsureWidgetWindowOnCurrentDispatcher");
                    Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
                    seen.Add(GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow"));
                }

                Assert.Single(seen.Distinct());
                Assert.True(seen[0].StartedHidden);
            }
            finally
            {
                DisposeQuietly(presenter);
                TryDeleteDirectory(dir);
            }
        });
    }

    /// <summary>
    /// A hide that expires while the agent is running: the widget comes back on the SAME window,
    /// and stops being special-cased the moment it genuinely comes up.
    /// </summary>
    [Fact]
    public void WhenTheHideExpires_TheWidgetComesBackWithoutARebuild()
    {
        string dir = NewTempDirectory();
        string hidePath = Path.Combine(dir, "widget-hidden.flag");
        var store = new UserDesktopWidgetVisibilityStore(hidePath);
        store.HideFor(TimeSpan.FromHours(1), DateTimeOffset.Now);

        UserDesktopWidgetPresenter? presenter = null;
        try
        {
            presenter = BuildPresenter(dir, hidePath);
            for (int i = 0; i < 5; i++)
            {
                presenter.TryUpdateWidgetState(BuildSampleWidgetState(), out _);
            }

            UserDesktopWidgetWindow hiddenWindow =
                GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow");
            Assert.True(OnWindow(hiddenWindow, static w => w.StartedHidden));

            // The hide expires; the maintenance tick notices the transition and brings it back.
            store.Show();
            presenter.ReevaluateVisibility();
            for (int i = 0; i < 5; i++)
            {
                presenter.TryUpdateWidgetState(BuildSampleWidgetState(), out _);
            }

            Assert.Same(
                hiddenWindow,
                GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow"));
            Assert.False(
                OnWindow(hiddenWindow, static w => w.StartedHidden),
                "once genuinely shown it goes back to being judged by IsLoaded");
        }
        finally
        {
            DisposeQuietly(presenter);
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Hiding a widget that is already up — the pre-existing path — still reuses one window, and
    /// that window was never StartedHidden because it came up through Show().
    /// </summary>
    [Fact]
    public void HidingAWidgetThatIsAlreadyShowing_StillReusesOneWindow()
    {
        string dir = NewTempDirectory();
        string hidePath = Path.Combine(dir, "widget-hidden.flag");
        var store = new UserDesktopWidgetVisibilityStore(hidePath);

        UserDesktopWidgetPresenter? presenter = null;
        try
        {
            presenter = BuildPresenter(dir, hidePath);
            presenter.TryUpdateWidgetState(BuildSampleWidgetState(), out _);

            UserDesktopWidgetWindow shown =
                GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow");
            Assert.False(
                OnWindow(shown, static w => w.StartedHidden),
                "it came up through Show(), not StartHidden()");

            store.HideFor(TimeSpan.FromHours(1), DateTimeOffset.Now);
            presenter.ReevaluateVisibility();
            for (int i = 0; i < 10; i++)
            {
                presenter.TryUpdateWidgetState(BuildSampleWidgetState(), out _);
            }

            Assert.Same(
                shown,
                GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow"));
        }
        finally
        {
            DisposeQuietly(presenter);
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Neither change may mask a real failure: a window that never went through StartHidden is
    /// still treated as a broken leftover and replaced, exactly as before.
    /// </summary>
    [Fact]
    public void AWindowThatNeverStartedHidden_IsStillTreatedAsABrokenLeftover()
    {
        string dir = NewTempDirectory();
        RunOnStaThread(() =>
        {
            UserDesktopWidgetPresenter? presenter = null;
            UserDesktopWidgetWindow? orphan = null;
            try
            {
                // Not hidden: the store has no flag file at all.
                presenter = BuildPresenter(dir, Path.Combine(dir, "no-hide.flag"));
                SetPrivateField(presenter, "_widgetDispatcher", Dispatcher.CurrentDispatcher);

                // Constructed, never shown, never marked hidden — the exact shape the never_loaded
                // close was written for.
                orphan = new UserDesktopWidgetWindow(
                    _ => { }, _ => { },
                    new UserDesktopWidgetPlacementStore(Path.Combine(dir, "orphan-placement.json")));
                Assert.False(orphan.IsLoaded);
                Assert.False(orphan.StartedHidden);

                SetPrivateField(presenter, "_widgetWindow", orphan);
                SetPrivateField(presenter, "_widgetReady", CreateReadySource());
                InvokePrivateMethod(presenter, "EnsureWidgetWindowOnCurrentDispatcher");

                UserDesktopWidgetWindow current =
                    GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow");
                Assert.NotSame(orphan, current);
            }
            finally
            {
                DisposeQuietly(presenter);
                CloseWindow(orphan);
                TryDeleteDirectory(dir);
            }
        });
    }

    // ── harness for the hidden-widget scenarios ────────────────────────────────────────────

    /// <summary>
    /// Drives the presenter the way the capture loop does: repeated state pushes, with the
    /// presenter owning its own widget thread. No dispatcher is planted by hand — planting one is
    /// what made the first version of these tests pass against a build that still leaked.
    /// </summary>
    private static void RunHiddenProductionScenario(
        TimeSpan hideFor,
        int pushes,
        Action<List<Thread>, List<UserDesktopWidgetWindow>> assert)
    {
        string dir = NewTempDirectory();
        string hidePath = Path.Combine(dir, "widget-hidden.flag");
        new UserDesktopWidgetVisibilityStore(hidePath).HideFor(hideFor, DateTimeOffset.Now);

        UserDesktopWidgetPresenter? presenter = null;
        try
        {
            presenter = BuildPresenter(dir, hidePath);

            var threads = new List<Thread>();
            var windows = new List<UserDesktopWidgetWindow>();
            for (int i = 0; i < pushes; i++)
            {
                presenter.TryUpdateWidgetState(BuildSampleWidgetState(), out _);
                threads.Add(GetPrivateField<Thread>(presenter, "_widgetThread"));
                windows.Add(GetPrivateField<UserDesktopWidgetWindow>(presenter, "_widgetWindow"));
            }

            Assert.Equal(pushes, windows.Count);
            assert(threads, windows);
        }
        finally
        {
            DisposeQuietly(presenter);
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Read a window property on the thread that OWNS the window. The presenter runs its widget on
    /// its own STA thread, so touching a DependencyProperty (IsLoaded) from the test thread throws.
    /// </summary>
    private static T OnWindow<T>(UserDesktopWidgetWindow window, Func<UserDesktopWidgetWindow, T> read)
        => window.Dispatcher.Invoke(() => read(window));

    private static TaskCompletionSource<bool> CreateReadySource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static UserDesktopWidgetState BuildSampleWidgetState()
        => new(
            Mode: UserDesktopWidgetMode.Monitoring,
            Title: "Ari",
            StatusText: "Monitoring",
            MessageSeverity: UserDesktopWidgetMessageSeverity.Neutral,
            FooterText: "Ready",
            PrimaryMetric: new UserDesktopWidgetMetric("Work today", "0m"),
            SecondaryMetric: new UserDesktopWidgetMetric("Last focus session", "0m"),
            TertiaryMetric: new UserDesktopWidgetMetric("Pressure", "0%"),
            CanPause: true,
            CanResume: false);

    private static UserDesktopWidgetPresenter BuildPresenter(string dir, string hidePath)
        => new(
            new UserDesktopWidgetRollout(WidgetEnabled: true, ForceToastFallback: false),
            new UserDesktopWidgetPlacementStore(Path.Combine(dir, "widget-placement.json")),
            new UserDesktopWidgetVisibilityStore(hidePath),
            new FakeBreakOverlayPresenter(),
            startWidgetThread: false);

    private static string NewTempDirectory()
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "porta-widget-hidden-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DisposeQuietly(UserDesktopWidgetPresenter? presenter)
    {
        try
        {
            presenter?.Dispose();
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field '{fieldName}'.");
        return (T)(field.GetValue(target) ?? throw new InvalidOperationException($"Field '{fieldName}' was null."));
    }

    private static void InvokePrivateMethod(object target, string methodName)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing method '{methodName}'.");
        method.Invoke(target, null);
    }

    private static void CloseWindow(UserDesktopWidgetWindow? window)
    {
        if (window == null)
        {
            return;
        }

        if (!window.IsClosingOrClosed)
        {
            window.AllowCloseForShutdown();
        }

        if (window.IsLoaded)
        {
            window.Close();
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

        Assert.True(completed.Wait(WindowCreationTimeout), "Timed out waiting for STA widget presenter test to finish.");
        Assert.True(thread.Join(ThreadSettleTimeout), "Timed out waiting for STA widget presenter test thread to join.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class FakeBreakOverlayPresenter : IUserBreakOverlayPresenter
    {
        public bool ShowResult { get; init; }
        public int PromptCalls { get; private set; }

        public bool TryPresentPrompt(
            UserInterventionPrompt prompt,
            Action<UserInterventionPromptResponse> onResponse,
            out string errorCode)
        {
            _ = prompt;
            _ = onResponse;
            PromptCalls++;
            errorCode = ShowResult ? string.Empty : "break_overlay_unavailable";
            return ShowResult;
        }

        public void Dispose()
        {
        }
    }
}
