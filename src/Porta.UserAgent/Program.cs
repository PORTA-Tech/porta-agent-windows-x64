using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Porta.Agent.Common.Runtime;
using Porta.Agent.Common.UserAgent;
using Porta.UserAgent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// The card preview harness. SHIPPED IN RELEASE since 1.2.146, deliberately.
//
// It was DEBUG-only, so the only way to look at a card in the installed build was to wait for the
// scheduler to fire one. Waiting a day to eyeball a card cost real time twice in the week of
// 2026-08-11, and the project picker shipped visibly broken partly because nobody could look at it.
//
// There is no UI, no button and no shortcut — an exact undocumented flag is the only way in, and
// anyone able to pass it can already run arbitrary code as this user, so it is not a privilege
// boundary. It reads nothing, writes nothing, and sends nothing: every prompt is a hardcoded
// literal and responses are discarded. See CheckInCardPreview.
//
// This runs BEFORE the single-instance lease and the host, so it cannot disturb a running agent.
// The exception hooks are wired first anyway, so a crash in here still leaves a trace.
if (Array.Exists(args, static a =>
        string.Equals(a, Porta.UserAgent.Interventions.CheckInCardPreview.Flag, StringComparison.OrdinalIgnoreCase)))
{
    AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        UserAgentFileLog.Error("preview_unhandled_exception", eventArgs.ExceptionObject as Exception);

    bool renderPngs = Array.Exists(args, static a =>
        string.Equals(a, Porta.UserAgent.Interventions.CheckInCardPreview.PngFlag, StringComparison.OrdinalIgnoreCase));
    Porta.UserAgent.Interventions.CheckInCardPreview.Run(renderPngs);
    return;
}

UserAgentStartupMetadata startupMetadata = UserAgentStartupMetadata.Parse(args);
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    UserAgentFileLog.Error("unhandled_exception", eventArgs.ExceptionObject as Exception);
};
TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    UserAgentFileLog.Error("unobserved_task_exception", eventArgs.Exception);
    eventArgs.SetObserved();
};

using System.Diagnostics.Process currentProcess = System.Diagnostics.Process.GetCurrentProcess();
UserAgentFileLog.Info(
    $"startup_requested source={startupMetadata.Source} launch_reason={startupMetadata.EffectiveLaunchReason} pid={currentProcess.Id} session={Math.Max(0, currentProcess.SessionId)} args={startupMetadata.RawArgsText}");

// The Control Center used to store the user's password on disk for "remember my
// sign-in". Purging it here rather than only in the Control Center is deliberate: the
// Control Center has no autostart entry, so on most machines nobody would ever open it
// and the password would sit there indefinitely. This process autostarts and runs as the
// user, so it can reach the CurrentUser-scoped file on the next sign-in.
if (Porta.Agent.Common.Identity.LegacyRememberedSignInPurge.PurgeStoredPassword())
{
    UserAgentFileLog.Info("legacy_remembered_signin_password_purged");
}

TryRegisterForApplicationRestart();

using SingleInstanceLease instanceLease = SingleInstanceLease.TryAcquire(
    $"Local\\PORTA.UserAgent.Session.{Math.Max(0, currentProcess.SessionId)}");
if (!instanceLease.HasHandle)
{
    UserAgentFileLog.Info(
        $"startup_skipped_single_instance source={startupMetadata.Source} launch_reason={startupMetadata.EffectiveLaunchReason} pid={currentProcess.Id} session={Math.Max(0, currentProcess.SessionId)}");
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IUserAgentSessionContextStore, UserAgentSessionContextStore>();
builder.Services.AddSingleton(startupMetadata);
builder.Services.AddHostedService<UserAgentHost>();

using IHost host = builder.Build();
try
{
    UserAgentFileLog.Info(
        $"host_run_begin source={startupMetadata.Source} launch_reason={startupMetadata.EffectiveLaunchReason} pid={currentProcess.Id} session={Math.Max(0, currentProcess.SessionId)}");
    await host.RunAsync().ConfigureAwait(false);
    UserAgentFileLog.Info(
        $"host_run_end source={startupMetadata.Source} launch_reason={startupMetadata.EffectiveLaunchReason} pid={currentProcess.Id} session={Math.Max(0, currentProcess.SessionId)}");
}
catch (Exception ex)
{
    UserAgentFileLog.Error(
        $"host_run_failed source={startupMetadata.Source} launch_reason={startupMetadata.EffectiveLaunchReason} pid={currentProcess.Id} session={Math.Max(0, currentProcess.SessionId)}",
        ex);
    throw;
}

static void TryRegisterForApplicationRestart()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    try
    {
        int hr = NativeMethods.RegisterApplicationRestart("--startup-source=windows-restart --launch-reason=windows-restart", 0);
        if (hr != 0)
        {
            UserAgentFileLog.Info($"application_restart_registration_failed hr=0x{hr:X8}");
        }
        else
        {
            UserAgentFileLog.Info("application_restart_registered");
        }
    }
    catch (Exception ex)
    {
        UserAgentFileLog.Error("application_restart_registration_failed", ex);
    }
}

[SupportedOSPlatform("windows")]
file static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern int RegisterApplicationRestart(string? pwzCommandline, int dwFlags);
}
