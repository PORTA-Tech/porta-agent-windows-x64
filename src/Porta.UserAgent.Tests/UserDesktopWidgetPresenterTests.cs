using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserDesktopWidgetPresenterTests
{
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

            Assert.True(created.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for widget window creation.");
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

            await worker.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Null(heartbeatFailure);
            Assert.False(heartbeatHealthy);
        }
        finally
        {
            presenter?.Dispose();
            release.Set();

            if (widgetOwnerThread is { IsAlive: true })
            {
                Assert.True(widgetOwnerThread.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for widget owner thread shutdown.");
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

                Assert.True(created.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for foreign widget window creation.");
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
                    Assert.True(widgetOwnerThread.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for foreign widget owner thread shutdown.");
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

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Timed out waiting for STA widget presenter test to finish.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for STA widget presenter test thread to join.");

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
