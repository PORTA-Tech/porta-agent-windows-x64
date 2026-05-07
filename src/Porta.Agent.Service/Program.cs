using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Porta.Agent.Common.Runtime;
using Porta.Agent.Service;

// Ensure Kestrel always has threads even while DB workers are blocked on SQLite I/O.
// The default minimum (ProcessorCount) gets exhausted when Defender scans DB files.
System.Threading.ThreadPool.SetMinThreads(workerThreads: 64, completionPortThreads: 64);

// Exclude the data directory from Windows Defender real-time scanning.
// Defender scans the WAL file (up to 2.46 GB) on every SQLite write commit.
// This causes multi-second fsync stalls that exhaust the thread pool.
// The service runs as LocalSystem which has rights to add Defender exclusions.
try
{
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = "-NoProfile -NonInteractive -Command \"Add-MpPreference -ExclusionPath 'C:\\ProgramData\\PortaAgent' -ErrorAction SilentlyContinue\"",
        CreateNoWindow = true,
        UseShellExecute = false
    })?.WaitForExit(3000);
}
catch { /* non-fatal — service continues without exclusion */ }

using SingleInstanceLease instanceLease = SingleInstanceLease.TryAcquire("Global\\PORTA.AriAgent.Service");
if (!instanceLease.HasHandle)
{
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<AgentService>();
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "AriAgent";
});

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
