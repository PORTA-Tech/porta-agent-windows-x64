using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Porta.Agent.Common.Runtime;
using Porta.Agent.Common.UserAgent;
using Porta.UserAgent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
