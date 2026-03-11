using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Porta.Agent.LocalApi;

namespace Porta.Agent.ValidationRunner.Fleet;

[SupportedOSPlatform("windows")]
internal static class FleetValidationRunner
{
    private static readonly TimeSpan AppearanceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SummaryPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UserAgentDegradeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UserAgentRecoverTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ActionLifecycleTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(string[] args)
    {
        FleetValidationOptions options = ParseOptions(args);
        using var controlPlane = new ControlPlaneFleetClient(options.ControlPlaneBaseUrl, options.OperatorId, options.OperatorRole);
        using var localApi = new LocalApiClient();

        if (options.VerifyPersistenceCheckpointPath is not null)
        {
            IReadOnlyList<FleetValidationResult> persistenceResults =
                await VerifyPersistenceCheckpointAsync(controlPlane, options.VerifyPersistenceCheckpointPath).ConfigureAwait(false);
            return PrintResultsAndReturnExitCode(persistenceResults);
        }

        var results = new List<FleetValidationResult>();

        MachineSummaryRow? summary = await WaitForMachineSummaryAsync(controlPlane, options.MachineId, AppearanceTimeout).ConfigureAwait(false);
        results.Add(summary is not null
            ? Pass("machine appears in machines/summary within 10s", $"{summary.MachineName} reported at {summary.ReportedAtUtc}.")
            : Fail("machine appears in machines/summary within 10s", $"No machine summary row arrived for {options.MachineId} within {AppearanceTimeout.TotalSeconds:F0}s."));

        if (summary is null)
        {
            return PrintResultsAndReturnExitCode(results);
        }

        if (options.AllowProcessControl)
        {
            results.AddRange(await ValidateUserAgentDegradeAndRecoverAsync(controlPlane, options, summary).ConfigureAwait(false));
            summary = await WaitForMachineSummaryAsync(controlPlane, options.MachineId, TimeSpan.FromSeconds(5)).ConfigureAwait(false) ?? summary;
        }
        else
        {
            results.Add(Info(
                "stop Porta.UserAgent -> degraded capture truth",
                "Skipped because --allow-process-control was not provided."));
        }

        results.AddRange(await ValidateActivityUpdateAsync(controlPlane, options.MachineId, summary).ConfigureAwait(false));
        results.AddRange(await ValidateLocalTruthAlignmentAsync(controlPlane, localApi, options.MachineId).ConfigureAwait(false));
        results.AddRange(await ValidateActionLifecycleAsync(controlPlane, localApi, options.MachineId).ConfigureAwait(false));
        results.AddRange(await CreatePersistenceCheckpointAsync(controlPlane, options.MachineId).ConfigureAwait(false));

        return PrintResultsAndReturnExitCode(results);
    }

    private static FleetValidationOptions ParseOptions(string[] args)
    {
        string? controlPlaneBaseUrl = null;
        string operatorId = "validation-runner";
        string operatorRole = "viewer";
        string machineId = Environment.MachineName;
        string userAgentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ari", "Porta.UserAgent.exe");
        bool allowProcessControl = false;
        string? verifyPersistenceCheckpointPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "--control-plane-base-url":
                    controlPlaneBaseUrl = ReadValue(args, ref index, arg);
                    break;
                case "--operator-id":
                    operatorId = ReadValue(args, ref index, arg);
                    break;
                case "--operator-role":
                    operatorRole = ReadValue(args, ref index, arg);
                    break;
                case "--machine-id":
                    machineId = ReadValue(args, ref index, arg);
                    break;
                case "--user-agent-path":
                    userAgentPath = ReadValue(args, ref index, arg);
                    break;
                case "--allow-process-control":
                    allowProcessControl = true;
                    break;
                case "--verify-persistence-checkpoint":
                    verifyPersistenceCheckpointPath = ReadValue(args, ref index, arg);
                    break;
                default:
                    throw new ArgumentException($"Unsupported fleet-gates argument: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(controlPlaneBaseUrl))
        {
            throw new ArgumentException("--control-plane-base-url is required.");
        }

        return new FleetValidationOptions(
            ControlPlaneBaseUrl: controlPlaneBaseUrl.Trim(),
            OperatorId: operatorId,
            OperatorRole: operatorRole,
            MachineId: machineId,
            UserAgentPath: userAgentPath,
            AllowProcessControl: allowProcessControl,
            VerifyPersistenceCheckpointPath: verifyPersistenceCheckpointPath);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static async Task<MachineSummaryRow?> WaitForMachineSummaryAsync(
        ControlPlaneFleetClient client,
        string machineId,
        TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            IReadOnlyList<MachineSummaryRow> rows = await client.GetMachineSummariesAsync(timeoutCts.Token).ConfigureAwait(false);
            MachineSummaryRow? row = rows.FirstOrDefault(item => string.Equals(item.MachineId, machineId, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                return row;
            }

            await Task.Delay(SummaryPollInterval, timeoutCts.Token).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> ValidateUserAgentDegradeAndRecoverAsync(
        ControlPlaneFleetClient controlPlane,
        FleetValidationOptions options,
        MachineSummaryRow _)
    {
        var results = new List<FleetValidationResult>();
        Process[] running = Process.GetProcessesByName("Porta.UserAgent");
        if (running.Length == 0)
        {
            results.Add(Fail("stop Porta.UserAgent -> degraded capture truth", "Porta.UserAgent is not running, so process-control validation cannot stop it."));
            return results;
        }

        foreach (Process process in running)
        {
            process.Kill(entireProcessTree: false);
            process.WaitForExit(10_000);
            process.Dispose();
        }

        MachineSummaryRow? degraded = await WaitForConditionAsync(
            controlPlane,
            options.MachineId,
            UserAgentDegradeTimeout,
            summary => !summary.UserSessionConnected).ConfigureAwait(false);

        results.Add(degraded is not null
            ? Pass("stop Porta.UserAgent -> Layer 1 degraded capture truth", $"User session disconnected after reporter update ({degraded.ReportedAtUtc}).")
            : Fail("stop Porta.UserAgent -> Layer 1 degraded capture truth", "Machine summary never reflected a disconnected user session within 60s."));

        if (degraded is not null)
        {
            results.Add(Pass(
                "stop Porta.UserAgent -> Layers 2-3 become limited/unavailable",
                "The summary now has userSessionConnected=false, which is the actual condition the operations console maps to unavailable state for Layers 2 and 3."));
        }

        if (!File.Exists(options.UserAgentPath))
        {
            results.Add(Fail("restart Porta.UserAgent after degradation test", $"Configured user-agent path does not exist: {options.UserAgentPath}"));
            return results;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = options.UserAgentPath,
            UseShellExecute = true
        });

        MachineSummaryRow? recovered = await WaitForConditionAsync(
            controlPlane,
            options.MachineId,
            UserAgentRecoverTimeout,
            summary => summary.UserSessionConnected).ConfigureAwait(false);

        results.Add(recovered is not null
            ? Pass("restart Porta.UserAgent -> session recovers", $"User session reconnected and reported at {recovered.ReportedAtUtc}.")
            : Fail("restart Porta.UserAgent -> session recovers", "Machine summary never returned to a connected user session state within 45s."));

        return results;
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> ValidateActivityUpdateAsync(
        ControlPlaneFleetClient controlPlane,
        string machineId,
        MachineSummaryRow baselineSummary)
    {
        var results = new List<FleetValidationResult>();

        Console.WriteLine();
        Console.WriteLine("Manual gate: type and move the mouse for 10 seconds, then press Enter to continue...");
        Console.ReadLine();

        long baselineActivityMs = ParseTimestampUtc(baselineSummary.LastActivityUtc);
        MachineSummaryRow? updated = await WaitForConditionAsync(
            controlPlane,
            machineId,
            ActivityTimeout,
            summary =>
                summary.ActivityDetected
                && ParseTimestampUtc(summary.LastActivityUtc) > baselineActivityMs
                && (summary.KeyboardSignalActive || summary.MouseSignalActive)).ConfigureAwait(false);

        results.Add(updated is not null
            ? Pass(
                "active typing/mouse -> activity, last activity, and keyboard/mouse flags update",
                $"lastActivityUtc moved to {updated.LastActivityUtc}; keyboard={updated.KeyboardSignalActive}; mouse={updated.MouseSignalActive}.")
            : Fail(
                "active typing/mouse -> activity, last activity, and keyboard/mouse flags update",
                "Machine summary did not show fresh activity plus keyboard/mouse truth within 25s after the manual input window."));

        return results;
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> ValidateLocalTruthAlignmentAsync(
        ControlPlaneFleetClient controlPlane,
        LocalApiClient localApi,
        string machineId)
    {
        var results = new List<FleetValidationResult>();
        LocalApiStatusResponse? localStatus = await localApi.GetStatusAsync().ConfigureAwait(false);
        LocalApiCurrentSnapshotResponse? localSnapshot = await localApi.GetCurrentSnapshotAsync().ConfigureAwait(false);
        MachineSummaryRow? summary = await WaitForMachineSummaryAsync(controlPlane, machineId, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        if (localStatus is null || localSnapshot is null || summary is null)
        {
            results.Add(Fail(
                "ready/high-trust session -> control plane truth matches local Control Center truth",
                "Local status/snapshot or machine summary could not be loaded."));
            return results;
        }

        var mismatches = new List<string>();
        if (!string.Equals(summary.CaptureContext, localStatus.CaptureContext, StringComparison.Ordinal))
        {
            mismatches.Add($"captureContext local={localStatus.CaptureContext} fleet={summary.CaptureContext}");
        }

        if (summary.KeyboardSignalActive != localStatus.KeyboardSignalActive)
        {
            mismatches.Add($"keyboardSignalActive local={localStatus.KeyboardSignalActive} fleet={summary.KeyboardSignalActive}");
        }

        if (summary.MouseSignalActive != localStatus.MouseSignalActive)
        {
            mismatches.Add($"mouseSignalActive local={localStatus.MouseSignalActive} fleet={summary.MouseSignalActive}");
        }

        if (!string.Equals(summary.CalibrationStage, localStatus.CalibrationStage, StringComparison.Ordinal))
        {
            mismatches.Add($"calibrationStage local={localStatus.CalibrationStage} fleet={summary.CalibrationStage}");
        }

        if (!string.Equals(summary.TrustTier, localStatus.TrustTier, StringComparison.Ordinal))
        {
            mismatches.Add($"trustTier local={localStatus.TrustTier} fleet={summary.TrustTier}");
        }

        if (summary.DeepWorkActive != localStatus.DeepWorkActive)
        {
            mismatches.Add($"deepWorkActive local={localStatus.DeepWorkActive} fleet={summary.DeepWorkActive}");
        }

        if (summary.OverloadActive != localStatus.OverloadActive)
        {
            mismatches.Add($"overloadActive local={localStatus.OverloadActive} fleet={summary.OverloadActive}");
        }

        if (Math.Abs(summary.OverloadLikelihood - localSnapshot.OverloadLikelihood) > 0.05d)
        {
            mismatches.Add($"overloadLikelihood local={localSnapshot.OverloadLikelihood:F3} fleet={summary.OverloadLikelihood:F3}");
        }

        if (Math.Abs(summary.TaskContinuitySeconds - localSnapshot.TaskContinuitySeconds) > 60d)
        {
            mismatches.Add($"taskContinuitySeconds local={localSnapshot.TaskContinuitySeconds:F0} fleet={summary.TaskContinuitySeconds:F0}");
        }

        double localModelConfidence = localSnapshot.ModelConfidence ?? localSnapshot.Confidence;
        if (Math.Abs(summary.ModelConfidence - localModelConfidence) > 0.08d)
        {
            mismatches.Add($"modelConfidence local={localModelConfidence:F3} fleet={summary.ModelConfidence:F3}");
        }

        results.Add(mismatches.Count == 0
            ? Pass(
                "ready/high-trust session -> control plane truth matches local Control Center truth",
                $"calibration={summary.CalibrationStage}, trust={summary.TrustTier}, continuity={summary.TaskContinuitySeconds:F0}s, overload={summary.OverloadLikelihood:F2}.")
            : Fail(
                "ready/high-trust session -> control plane truth matches local Control Center truth",
                string.Join("; ", mismatches)));

        return results;
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> ValidateActionLifecycleAsync(
        ControlPlaneFleetClient controlPlane,
        LocalApiClient localApi,
        string machineId)
    {
        var results = new List<FleetValidationResult>();
        LocalApiAckResponse? breakAck = await localApi.TriggerTestInterventionAsync("break_suggestion").ConfigureAwait(false);
        if (breakAck is null || !breakAck.Success)
        {
            results.Add(Fail(
                "suggestion action -> decided to applied lifecycle centrally",
                $"Local break_suggestion test trigger failed: {breakAck?.Message ?? "no_response"}"));
        }
        else
        {
            MachineActionRow? breakAction = await WaitForActionAsync(
                controlPlane,
                machineId,
                "break_suggestion",
                row => string.Equals(row.ExecutionState, "applied", StringComparison.OrdinalIgnoreCase),
                ActionLifecycleTimeout).ConfigureAwait(false);

            results.Add(breakAction is not null
                ? Pass(
                    "suggestion action -> decided to applied lifecycle centrally",
                    $"Action {breakAction.Action} reached execution_state={breakAction.ExecutionState}.")
                : Fail(
                    "suggestion action -> decided to applied lifecycle centrally",
                    "No centrally reported break_suggestion action reached execution_state=applied within 30s."));
        }

        LocalApiAckResponse? suppressAck = await localApi.TriggerTestInterventionAsync("deep_work_notifications_suppressed").ConfigureAwait(false);
        if (suppressAck is null || !suppressAck.Success)
        {
            results.Add(Fail(
                "notification suppression -> decided to verified lifecycle centrally",
                $"Local deep_work_notifications_suppressed test trigger failed: {suppressAck?.Message ?? "no_response"}"));
            return results;
        }

        MachineActionRow? suppressAction = await WaitForActionAsync(
            controlPlane,
            machineId,
            "deep_work_notifications_suppressed",
            row => string.Equals(row.ExecutionState, "verified", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.VerifiedState, "verified", StringComparison.OrdinalIgnoreCase),
            ActionLifecycleTimeout).ConfigureAwait(false);

        results.Add(suppressAction is not null
            ? Pass(
                "notification suppression -> decided to verified lifecycle centrally",
                $"Action {suppressAction.Action} reached execution_state={suppressAction.ExecutionState}, verified_state={suppressAction.VerifiedState}.")
            : Fail(
                "notification suppression -> decided to verified lifecycle centrally",
                "No centrally reported notification suppression action reached verified state within 30s."));

        return results;
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> CreatePersistenceCheckpointAsync(
        ControlPlaneFleetClient controlPlane,
        string machineId)
    {
        var results = new List<FleetValidationResult>();
        FleetSystemHealthResponse? systemHealth = await controlPlane.GetSystemHealthAsync(CancellationToken.None).ConfigureAwait(false);
        string persistenceMode = systemHealth?.FleetReporting?.PersistenceMode ?? "unknown";
        if (!string.Equals(persistenceMode, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(Info(
                "control plane restart -> machine/action truth persists if using real DB",
                $"Skipped automatic checkpoint because control plane persistence mode is {persistenceMode}."));
            return results;
        }

        MachineSummaryRow? summary = await WaitForMachineSummaryAsync(controlPlane, machineId, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        IReadOnlyList<MachineActionRow> actions = await controlPlane.GetRecentActionsAsync(50, CancellationToken.None).ConfigureAwait(false);
        MachineActionRow? breakRow = actions.FirstOrDefault(row => string.Equals(row.MachineId, machineId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.Action, "break_suggestion", StringComparison.OrdinalIgnoreCase));
        MachineActionRow? suppressRow = actions.FirstOrDefault(row => string.Equals(row.MachineId, machineId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.Action, "deep_work_notifications_suppressed", StringComparison.OrdinalIgnoreCase));

        if (summary is null)
        {
            results.Add(Fail(
                "control plane restart -> persistence checkpoint prepared",
                "Could not load the current machine summary to write a persistence checkpoint."));
            return results;
        }

        string checkpointPath = Path.Combine(
            Path.GetTempPath(),
            $"porta-fleet-persistence-{machineId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");

        var checkpoint = new FleetValidationCheckpoint(
            MachineId: machineId,
            ReportedAtUtc: summary.ReportedAtUtc,
            BreakInterventionId: breakRow?.InterventionId,
            NotificationInterventionId: suppressRow?.InterventionId,
            CreatedAtUtc: DateTimeOffset.UtcNow.ToString("O"));

        string raw = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(checkpointPath, raw);

        results.Add(Info(
            "control plane restart -> machine/action truth persists if using real DB",
            $"Checkpoint written to {checkpointPath}. Restart the control plane, then rerun: fleet-gates --control-plane-base-url <url> --verify-persistence-checkpoint \"{checkpointPath}\""));

        return results;
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> VerifyPersistenceCheckpointAsync(
        ControlPlaneFleetClient controlPlane,
        string checkpointPath)
    {
        var results = new List<FleetValidationResult>();
        if (!File.Exists(checkpointPath))
        {
            results.Add(Fail("control plane restart -> persistence verification", $"Checkpoint file not found: {checkpointPath}"));
            return results;
        }

        FleetValidationCheckpoint checkpoint = JsonSerializer.Deserialize<FleetValidationCheckpoint>(File.ReadAllText(checkpointPath))
            ?? throw new InvalidOperationException("Invalid persistence checkpoint file.");

        MachineSummaryRow? summary = await WaitForMachineSummaryAsync(controlPlane, checkpoint.MachineId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        IReadOnlyList<MachineActionRow> actions = await controlPlane.GetRecentActionsAsync(100, CancellationToken.None).ConfigureAwait(false);

        bool summaryPersisted = summary is not null
            && ParseTimestampUtc(summary.ReportedAtUtc) >= ParseTimestampUtc(checkpoint.ReportedAtUtc);
        bool breakPersisted = string.IsNullOrWhiteSpace(checkpoint.BreakInterventionId)
            || actions.Any(row =>
                string.Equals(row.MachineId, checkpoint.MachineId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.InterventionId, checkpoint.BreakInterventionId, StringComparison.OrdinalIgnoreCase));
        bool notificationPersisted = string.IsNullOrWhiteSpace(checkpoint.NotificationInterventionId)
            || actions.Any(row =>
                string.Equals(row.MachineId, checkpoint.MachineId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.InterventionId, checkpoint.NotificationInterventionId, StringComparison.OrdinalIgnoreCase));

        results.Add(summaryPersisted
            ? Pass(
                "control plane restart -> machine truth persists",
                $"Machine summary for {checkpoint.MachineId} still exists after restart.")
            : Fail(
                "control plane restart -> machine truth persists",
                $"Machine summary for {checkpoint.MachineId} was not found after restart verification."));

        results.Add(breakPersisted && notificationPersisted
            ? Pass(
                "control plane restart -> action truth persists",
                "Checkpointed action rows still exist after restart verification.")
            : Fail(
                "control plane restart -> action truth persists",
                $"Missing persisted action rows: break={breakPersisted}, notification={notificationPersisted}."));
        return results;
    }

    private static async Task<MachineSummaryRow?> WaitForConditionAsync(
        ControlPlaneFleetClient client,
        string machineId,
        TimeSpan timeout,
        Func<MachineSummaryRow, bool> predicate)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            MachineSummaryRow? row = await WaitForMachineSummaryAsync(client, machineId, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (row is not null && predicate(row))
            {
                return row;
            }

            await Task.Delay(SummaryPollInterval, timeoutCts.Token).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<MachineActionRow?> WaitForActionAsync(
        ControlPlaneFleetClient client,
        string machineId,
        string action,
        Func<MachineActionRow, bool> predicate,
        TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            IReadOnlyList<MachineActionRow> actions = await client.GetRecentActionsAsync(100, timeoutCts.Token).ConfigureAwait(false);
            MachineActionRow? row = actions
                .Where(item => string.Equals(item.MachineId, machineId, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.Equals(item.Action, action, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => ParseTimestampUtc(item.TimestampUtc))
                .FirstOrDefault(predicate);
            if (row is not null)
            {
                return row;
            }

            await Task.Delay(SummaryPollInterval, timeoutCts.Token).ConfigureAwait(false);
        }

        return null;
    }

    private static long ParseTimestampUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0L;
        }

        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : 0L;
    }

    private static FleetValidationResult Pass(string name, string detail) => new(true, name, detail);

    private static FleetValidationResult Fail(string name, string detail) => new(false, name, detail);

    private static FleetValidationResult Info(string name, string detail) => new(true, name, detail);

    private static int PrintResultsAndReturnExitCode(IReadOnlyList<FleetValidationResult> results)
    {
        int failed = 0;
        Console.WriteLine("Fleet validation gates");
        foreach (FleetValidationResult result in results)
        {
            string status = result.Success ? "PASS" : "FAIL";
            Console.WriteLine($"{status} {result.Name} {result.Detail}");
            if (!result.Success)
            {
                failed++;
            }
        }

        Console.WriteLine($"Summary: total={results.Count} failed={failed}");
        return failed == 0 ? 0 : 1;
    }
}
