using System.Reflection;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserDesktopWidgetPresenterTests
{
    [Fact]
    public async Task HeartbeatHealthCheck_DoesNotTouchCrossThreadWindowState()
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
            presenter = new UserDesktopWidgetPresenter(
                new UserDesktopWidgetRollout(WidgetEnabled: true, ForceToastFallback: false),
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory, "widget-placement.json")),
                new UserDesktopWidgetVisibilityStore(Path.Combine(tempDirectory, "widget-hidden.flag")),
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
            SetPrivateField(presenter, "_widgetHealthState", 2);

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
            Assert.True(heartbeatHealthy);
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

    private static void SetPrivateField<TValue>(object target, string fieldName, TValue value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field '{fieldName}'.");
        field.SetValue(target, value);
    }
}
