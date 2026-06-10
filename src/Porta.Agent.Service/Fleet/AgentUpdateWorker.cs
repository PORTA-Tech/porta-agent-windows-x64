using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porta.Agent.Common.Updates;
using Porta.Agent.Core;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Silent fleet auto-update. Runs inside the SYSTEM service: checks the control
/// plane's release manifest, downloads and verifies new MSIs, waits for a quiet
/// moment, and installs without any user action. Every safety invariant from
/// AutoUpdate_Plan.md is enforced here:
///   1. No SHA-256 in manifest -> no install, ever.
///   2. Never equal/lower versions; a version that failed 3x here is blacklisted.
///   3. One update in flight per machine (lock file with staleness timeout).
///   4. Never install during active use - idle/no-session/3am-fallback only.
///   5. msiexec is transactional - a failed install leaves the old version running.
///   6. Staged rollout gate, stable per machine.
///   7. The whole tick is wrapped in try/catch - an updater bug can never take
///      down capture or the service.
///   8. Outcome is always written to the status file the machine summary reports.
/// </summary>
internal sealed class AgentUpdateWorker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LockStaleAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ForcedInstallAfter = TimeSpan.FromHours(24);
    private const int ForcedInstallHourLocal = 3;
    private const int MaxAttemptsPerVersion = 3;
    private const long MaxDownloadBytes = 500L * 1024 * 1024;
    private const string ServiceName = "AriAgent";

    private readonly ControlPlaneClient _controlPlaneClient;
    private readonly IAgentCoreController _controller;
    private readonly ILogger<AgentUpdateWorker> _logger;
    private readonly string _machineId = Environment.MachineName;
    private DateTimeOffset _nextManifestCheckUtc = DateTimeOffset.MinValue;
    private PendingUpdate? _pending;

    public AgentUpdateWorker(
        ControlPlaneClient controlPlaneClient,
        IAgentCoreController controller,
        ILogger<AgentUpdateWorker> logger)
    {
        _controlPlaneClient = controlPlaneClient;
        _controller = controller;
        _logger = logger;
        // First check 2-15 min after service start, then every 6h with jitter so the
        // fleet never stampedes the manifest endpoint or the download host.
        _nextManifestCheckUtc = DateTimeOffset.UtcNow.AddMinutes(2 + Random.Shared.Next(0, 14));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_controlPlaneClient.IsEnabled)
        {
            LogUpdate($"agent_update_disabled reason={_controlPlaneClient.DisabledReason}");
            return;
        }

        using var timer = new PeriodicTimer(TickInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Invariant 7: an updater bug can never take down the service.
                _logger.LogError(ex, "Agent update tick failed. Continuing.");
                LogUpdate($"agent_update_tick_failed error={ex.GetType().Name}:{TrimForLog(ex.Message)}");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        if (_pending is null && nowUtc >= _nextManifestCheckUtc)
        {
            _nextManifestCheckUtc = nowUtc.Add(CheckInterval).AddMinutes(Random.Shared.Next(0, 31));
            await CheckManifestAsync(token).ConfigureAwait(false);
        }

        if (_pending is { } pending)
        {
            await TryInstallPendingAsync(pending, token).ConfigureAwait(false);
        }
    }

    private async Task CheckManifestAsync(CancellationToken token)
    {
        WindowsReleaseManifestPayload? manifest = await _controlPlaneClient.GetLatestWindowsReleaseAsync(token).ConfigureAwait(false);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.MsiUrl))
        {
            return;
        }

        Version? target = ParseVersion(manifest.Version);
        Version? installed = GetInstalledVersion();
        if (target is null || installed is null)
        {
            LogUpdate($"agent_update_version_unreadable manifest={manifest.Version} installed={installed?.ToString() ?? "unknown"}");
            return;
        }

        // Invariant 2: only ever move forward.
        if (target <= installed)
        {
            return;
        }

        // Invariant 1: no hash, no install. Ever.
        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            LogUpdate($"agent_update_rejected version={manifest.Version} reason=missing_sha256");
            return;
        }

        // Invariant 6: stable staged-rollout gate.
        if (!IsInsideRolloutGate(manifest.RolloutPercent))
        {
            LogUpdate($"agent_update_outside_rollout version={manifest.Version} rollout={manifest.RolloutPercent}");
            return;
        }

        // Invariant 2: blacklist after repeated failures.
        UpdateStateFile state = ReadStateFile();
        if (string.Equals(state.Version, manifest.Version, StringComparison.OrdinalIgnoreCase)
            && state.Attempts >= MaxAttemptsPerVersion)
        {
            LogUpdate($"agent_update_blacklisted version={manifest.Version} attempts={state.Attempts}");
            return;
        }

        if (!Uri.TryCreate(manifest.MsiUrl, UriKind.Absolute, out Uri? downloadUri)
            || !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            LogUpdate($"agent_update_rejected version={manifest.Version} reason=non_https_url");
            return;
        }

        LogUpdate($"agent_update_available installed={installed} target={manifest.Version} rollout={manifest.RolloutPercent}");

        string installerPath;
        try
        {
            installerPath = await DownloadAndVerifyAsync(manifest, downloadUri, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordAttempt(manifest.Version!);
            LogUpdate($"agent_update_download_failed version={manifest.Version} error={ex.GetType().Name}:{TrimForLog(ex.Message)}");
            return;
        }

        _pending = new PendingUpdate(manifest, installerPath, DateTimeOffset.UtcNow);
        LogUpdate($"agent_update_ready version={manifest.Version} waiting_for=quiet_window");
    }

    private async Task<string> DownloadAndVerifyAsync(
        WindowsReleaseManifestPayload manifest,
        Uri downloadUri,
        CancellationToken token)
    {
        string updatesRoot = GetUpdatesRoot();
        string safeVersion = SanitizeForFileName(manifest.Version!);
        string targetPath = Path.Combine(updatesRoot, $"AriAgentInstaller-{safeVersion}.msi");
        string tempPath = targetPath + ".download";

        // Reuse a previously verified download when present (service restarts, retries).
        if (File.Exists(targetPath) && await Sha256MatchesAsync(targetPath, manifest.Sha256!).ConfigureAwait(false))
        {
            VerifyMsiProductVersion(targetPath, manifest.Version!);
            return targetPath;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using HttpResponseMessage response = await httpClient
            .GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
        {
            throw new InvalidOperationException("Update download exceeds the size limit.");
        }

        try
        {
            await using (FileStream destination = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(destination, token).ConfigureAwait(false);
                if (destination.Length > MaxDownloadBytes)
                {
                    throw new InvalidOperationException("Update download exceeds the size limit.");
                }
            }

            if (!await Sha256MatchesAsync(tempPath, manifest.Sha256!).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Downloaded update does not match the published SHA-256 checksum.");
            }

            VerifyMsiProductVersion(tempPath, manifest.Version!);
            File.Move(tempPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private async Task TryInstallPendingAsync(PendingUpdate pending, CancellationToken token)
    {
        _ = token;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        // Invariant 3: one update in flight per machine.
        if (IsInstallLockHeld(nowUtc))
        {
            return;
        }

        // Invariant 4: only at a quiet moment - or the 3am fallback after 24h.
        if (!IsQuietWindow(nowUtc) && !IsForcedWindow(pending, nowUtc))
        {
            return;
        }

        // Re-verify the artifact immediately before launch.
        if (!File.Exists(pending.InstallerPath)
            || !await Sha256MatchesAsync(pending.InstallerPath, pending.Manifest.Sha256!).ConfigureAwait(false))
        {
            LogUpdate($"agent_update_artifact_invalid version={pending.Manifest.Version} action=redownload_next_check");
            _pending = null;
            _nextManifestCheckUtc = DateTimeOffset.MinValue;
            return;
        }

        RecordAttempt(pending.Manifest.Version!);
        WriteInstallLock(nowUtc);

        try
        {
            LaunchDetachedInstaller(pending);
            LogUpdate($"agent_update_install_launched version={pending.Manifest.Version}");
            _pending = null;
            // The detached script now stops this service; nothing further to do here.
        }
        catch (Exception ex)
        {
            ReleaseInstallLock();
            LogUpdate($"agent_update_launch_failed version={pending.Manifest.Version} error={ex.GetType().Name}:{TrimForLog(ex.Message)}");
        }
    }

    private void LaunchDetachedInstaller(PendingUpdate pending)
    {
        string updatesRoot = GetUpdatesRoot();
        string safeVersion = SanitizeForFileName(pending.Manifest.Version!);
        string helperScriptPath = Path.Combine(updatesRoot, "windows-update.install.ps1");
        string contextPath = Path.Combine(updatesRoot, $"windows-update-{safeVersion}.context.json");
        string logPath = Path.Combine(updatesRoot, $"windows-update-{safeVersion}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.log");
        string statusPath = GetStatusPath();

        TryDelete(statusPath);
        File.WriteAllText(helperScriptPath, WindowsUpdateInstallScriptBuilder.Build());

        var context = new
        {
            InstallerPath = pending.InstallerPath,
            ExpectedSha256 = pending.Manifest.Sha256!.Trim().ToLowerInvariant(),
            RequireSha256Validation = true,
            RequireSignatureValidation = pending.Manifest.RequireSignature,
            Version = pending.Manifest.Version,
            ServiceName,
            CurrentControlCenterPid = 0,
            CurrentControlCenterPath = string.Empty,
            RestartControlCenterPath = string.Empty,
            StatusPath = statusPath,
            LogPath = logPath
        };
        File.WriteAllText(contextPath, JsonSerializer.Serialize(context));

        string powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershellPath))
        {
            powershellPath = "powershell.exe";
        }

        // Detached child: survives this service being stopped by the script.
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helperScriptPath}\" -ContextPath \"{contextPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updatesRoot
        };
        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start the update installer process.");
        }
    }

    // ── Quiet-window policy ─────────────────────────────────────────────────

    private bool IsQuietWindow(DateTimeOffset nowUtc)
    {
        try
        {
            AgentServiceStatusSnapshot status = _controller.GetStatus();

            // No interactive user session connected -> machine is unattended.
            if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.Ordinal))
            {
                return true;
            }

            // Locked sessions and walked-away users both stop producing input.
            DateTimeOffset? lastInput = status.LastInputEventUtc;
            return lastInput is null || (nowUtc - lastInput.Value) >= IdleThreshold;
        }
        catch
        {
            // If the runtime state is unreadable, err on the side of NOT installing.
            return false;
        }
    }

    private static bool IsForcedWindow(PendingUpdate pending, DateTimeOffset nowUtc)
    {
        if ((nowUtc - pending.ReadyAtUtc) < ForcedInstallAfter)
        {
            return false;
        }

        return DateTimeOffset.Now.Hour == ForcedInstallHourLocal;
    }

    // ── Rollout gate ────────────────────────────────────────────────────────

    private bool IsInsideRolloutGate(int rolloutPercent)
    {
        int percent = Math.Clamp(rolloutPercent, 0, 100);
        if (percent >= 100)
        {
            return true;
        }

        if (percent <= 0)
        {
            return false;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(_machineId.ToUpperInvariant()));
        int bucket = (hash[0] << 8 | hash[1]) % 100;
        return bucket < percent;
    }

    // ── Verification helpers ────────────────────────────────────────────────

    private static async Task<bool> Sha256MatchesAsync(string path, string expected)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        string actual = Convert.ToHexString(hash);
        return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads ProductVersion from the MSI database — rejects mislabeled uploads.</summary>
    private static void VerifyMsiProductVersion(string msiPath, string expectedVersion)
    {
        _ = NativeMethods.MsiSetInternalUI(2 /* INSTALLUILEVEL_NONE */, IntPtr.Zero);
        uint openResult = NativeMethods.MsiOpenPackageEx(msiPath, 1 /* IGNOREMACHINESTATE */, out IntPtr handle);
        if (openResult != 0)
        {
            throw new InvalidOperationException($"Cannot open the MSI to verify its version (code {openResult}).");
        }

        try
        {
            var builder = new StringBuilder(64);
            int length = builder.Capacity;
            uint getResult = NativeMethods.MsiGetProductProperty(handle, "ProductVersion", builder, ref length);
            if (getResult != 0)
            {
                throw new InvalidOperationException($"Cannot read ProductVersion from the MSI (code {getResult}).");
            }

            Version? actual = ParseVersion(builder.ToString());
            Version? expected = ParseVersion(expectedVersion);
            if (actual is null || expected is null
                || actual.Major != expected.Major
                || actual.Minor != expected.Minor
                || actual.Build != expected.Build)
            {
                throw new InvalidOperationException(
                    $"MSI ProductVersion '{builder}' does not match the manifest version '{expectedVersion}'.");
            }
        }
        finally
        {
            _ = NativeMethods.MsiCloseHandle(handle);
        }
    }

    private static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim().TrimStart('v', 'V');
        int dash = trimmed.IndexOf('-');
        if (dash > 0)
        {
            trimmed = trimmed[..dash];
        }

        if (Version.TryParse(trimmed, out Version? parsed))
        {
            return parsed;
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major)
            ? new Version(major, 0)
            : null;
    }

    private static Version? GetInstalledVersion()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return null;
            }

            FileVersionInfo info = FileVersionInfo.GetVersionInfo(processPath);
            return ParseVersion(info.FileVersion);
        }
        catch
        {
            return null;
        }
    }

    // ── State, lock, status files ───────────────────────────────────────────

    private static string GetUpdatesRoot()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PortaAgent",
            "updates");
        Directory.CreateDirectory(root);
        return root;
    }

    internal static string GetStatusPath() => Path.Combine(GetUpdatesRoot(), "windows-update.status.json");

    private static string GetStateFilePath() => Path.Combine(GetUpdatesRoot(), "agent-update.state.json");

    private static string GetLockFilePath() => Path.Combine(GetUpdatesRoot(), "agent-update.lock");

    private UpdateStateFile ReadStateFile()
    {
        try
        {
            string path = GetStateFilePath();
            if (!File.Exists(path))
            {
                return new UpdateStateFile(null, 0);
            }

            return JsonSerializer.Deserialize<UpdateStateFile>(File.ReadAllText(path)) ?? new UpdateStateFile(null, 0);
        }
        catch
        {
            return new UpdateStateFile(null, 0);
        }
    }

    private void RecordAttempt(string version)
    {
        try
        {
            UpdateStateFile state = ReadStateFile();
            int attempts = string.Equals(state.Version, version, StringComparison.OrdinalIgnoreCase)
                ? state.Attempts + 1
                : 1;
            File.WriteAllText(GetStateFilePath(), JsonSerializer.Serialize(new UpdateStateFile(version, attempts)));
        }
        catch
        {
        }
    }

    private static bool IsInstallLockHeld(DateTimeOffset nowUtc)
    {
        try
        {
            string path = GetLockFilePath();
            if (!File.Exists(path))
            {
                return false;
            }

            string raw = File.ReadAllText(path).Trim();
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset writtenAt)
                && (nowUtc - writtenAt) < LockStaleAfter)
            {
                return true;
            }

            // Stale lock from a crashed attempt - reclaim it.
            File.Delete(path);
            return false;
        }
        catch
        {
            return true; // Cannot evaluate the lock -> assume held, never double-install.
        }
    }

    private static void WriteInstallLock(DateTimeOffset nowUtc)
    {
        File.WriteAllText(GetLockFilePath(), nowUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void ReleaseInstallLock()
    {
        TryDelete(GetLockFilePath());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string SanitizeForFileName(string version)
    {
        string sanitized = version.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }

        return sanitized;
    }

    private static string TrimForLog(string message)
    {
        string single = message.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length <= 160 ? single : single[..160];
    }

    /// <summary>
    /// Appends to the same log file whose tail flows into the machine summary,
    /// so update progress is visible from the operations console remotely.
    /// </summary>
    private static void LogUpdate(string message)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PortaAgent",
                "user-agent.log");
            File.AppendAllText(path, $"[{DateTimeOffset.UtcNow:O}] [INFO] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private sealed record PendingUpdate(
        WindowsReleaseManifestPayload Manifest,
        string InstallerPath,
        DateTimeOffset ReadyAtUtc);

    private sealed record UpdateStateFile(string? Version, int Attempts);

    private static class NativeMethods
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern int MsiSetInternalUI(int dwUILevel, IntPtr phWnd);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiOpenPackageEx(string szPackagePath, uint dwOptions, out IntPtr hProduct);

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        internal static extern uint MsiGetProductProperty(IntPtr hProduct, string szProperty, StringBuilder lpValueBuf, ref int pcchValueBuf);

        [DllImport("msi.dll")]
        internal static extern uint MsiCloseHandle(IntPtr hAny);
    }
}
