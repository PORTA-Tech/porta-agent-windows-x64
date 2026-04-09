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
    private static readonly TimeSpan ControlCenterClosureTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan ActionLifecycleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdentityLinkTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DeviceInventoryTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ValidationReportExpiry = TimeSpan.FromHours(24);
    private const string IdentityRolloutSuiteName = "identity_rollout";
    private const string TelemetryLevelAccepted = "enhanced";
    private const string TelemetryLevelRejected = "essential";

    private sealed record IdentityGateResult(
        string GateId,
        FleetValidationResult Result,
        string CheckedAtUtc)
    {
        public RolloutValidationGatePayload ToPayload()
            => new(
                GateId: GateId,
                Name: Result.Name,
                Success: Result.Success,
                Detail: Result.Detail,
                CheckedAtUtc: CheckedAtUtc);
    }

    private sealed record IdentityValidationOutcome(
        IReadOnlyList<IdentityGateResult> Gates,
        MachineSummaryRow? LatestSummary,
        string? DeviceId,
        string? AccountId,
        string? OrgId);

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

        if (!options.SkipIdentityRolloutGates)
        {
            IdentityValidationOutcome identityValidation =
                await ValidateIdentityRolloutGatesAsync(controlPlane, localApi, options, summary).ConfigureAwait(false);
            results.AddRange(identityValidation.Gates.Select(gate => gate.Result));

            FleetValidationResult publishValidation = await PublishIdentityRolloutReportAsync(controlPlane, identityValidation).ConfigureAwait(false);
            results.Add(publishValidation);

            FleetValidationResult rolloutGuardrailValidation = await ValidatePublishedRolloutGuardrailAsync(controlPlane).ConfigureAwait(false);
            results.Add(rolloutGuardrailValidation);

            summary = identityValidation.LatestSummary
                ?? await WaitForMachineSummaryAsync(controlPlane, options.MachineId, TimeSpan.FromSeconds(5)).ConfigureAwait(false)
                ?? summary;
        }
        else
        {
            results.Add(Info(
                "identity rollout validation gates",
                "Skipped because --skip-identity-rollout-gates was provided."));
        }

        results.Add(ValidatePrimaryStartupPath(summary));

        if (options.AllowProcessControl)
        {
            results.AddRange(await ValidateUserAgentDegradeAndRecoverAsync(controlPlane, options, summary).ConfigureAwait(false));
            summary = await WaitForMachineSummaryAsync(controlPlane, options.MachineId, TimeSpan.FromSeconds(5)).ConfigureAwait(false) ?? summary;
            results.AddRange(await ValidateControlCenterClosedAsync(controlPlane, localApi, options.MachineId).ConfigureAwait(false));
            summary = await WaitForMachineSummaryAsync(controlPlane, options.MachineId, TimeSpan.FromSeconds(5)).ConfigureAwait(false) ?? summary;
        }
        else
        {
            results.Add(Info(
                "kill Porta.UserAgent -> service watchdog relaunches interactive session agent",
                "Skipped because --allow-process-control was not provided."));
            results.Add(Info(
                "close Control Center -> capture stays healthy",
                "Skipped because --allow-process-control was not provided."));
        }

        results.AddRange(await ValidateActivityUpdateAsync(controlPlane, options.MachineId, summary).ConfigureAwait(false));
        summary = await WaitForMachineSummaryAsync(controlPlane, options.MachineId, TimeSpan.FromSeconds(5)).ConfigureAwait(false) ?? summary;
        results.AddRange(await ValidateRejectedPacketOwnershipAsync(localApi).ConfigureAwait(false));
        results.AddRange(await ValidateIdleConnectedAsync(controlPlane, options.MachineId, summary).ConfigureAwait(false));
        results.AddRange(await ValidateLocalTruthAlignmentAsync(controlPlane, localApi, options.MachineId).ConfigureAwait(false));
        results.AddRange(await ValidateActionLifecycleAsync(controlPlane, localApi, options.MachineId).ConfigureAwait(false));
        results.AddRange(await CreatePersistenceCheckpointAsync(controlPlane, options.MachineId).ConfigureAwait(false));

        return PrintResultsAndReturnExitCode(results);
    }

    private static FleetValidationOptions ParseOptions(string[] args)
    {
        string? controlPlaneBaseUrl = null;
        string operatorId = "validation-runner";
        string operatorRole = "operator";
        string machineId = Environment.MachineName;
        string userAgentPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ari", "Porta.UserAgent.exe");
        bool allowProcessControl = false;
        string? verifyPersistenceCheckpointPath = null;
        string? identityUserId = null;
        string? identityPassword = null;
        string? identityOrgId = null;
        bool allowIdentityRelink = false;
        bool skipIdentityRolloutGates = false;

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
                case "--identity-user-id":
                    identityUserId = ReadValue(args, ref index, arg);
                    break;
                case "--identity-password":
                    identityPassword = ReadValue(args, ref index, arg);
                    break;
                case "--identity-org-id":
                    identityOrgId = ReadValue(args, ref index, arg);
                    break;
                case "--allow-identity-relink":
                    allowIdentityRelink = true;
                    break;
                case "--skip-identity-rollout-gates":
                    skipIdentityRolloutGates = true;
                    break;
                default:
                    throw new ArgumentException($"Unsupported fleet-gates argument: {arg}");
            }
        }

        if (string.IsNullOrWhiteSpace(controlPlaneBaseUrl))
        {
            throw new ArgumentException("--control-plane-base-url is required.");
        }

        if (verifyPersistenceCheckpointPath is null && !skipIdentityRolloutGates)
        {
            if (string.IsNullOrWhiteSpace(identityUserId) || string.IsNullOrWhiteSpace(identityPassword))
            {
                throw new ArgumentException(
                    "--identity-user-id and --identity-password are required unless --skip-identity-rollout-gates is provided.");
            }

            if (!string.Equals(operatorRole, "operator", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(operatorRole, "admin", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "--operator-role must be operator or admin when identity rollout gates are enabled so the validation report can be published.");
            }
        }

        return new FleetValidationOptions(
            ControlPlaneBaseUrl: controlPlaneBaseUrl.Trim(),
            OperatorId: operatorId,
            OperatorRole: operatorRole,
            MachineId: machineId,
            UserAgentPath: userAgentPath,
            AllowProcessControl: allowProcessControl,
            VerifyPersistenceCheckpointPath: verifyPersistenceCheckpointPath,
            IdentityUserId: identityUserId?.Trim(),
            IdentityPassword: identityPassword,
            IdentityOrgId: identityOrgId?.Trim(),
            AllowIdentityRelink: allowIdentityRelink,
            SkipIdentityRolloutGates: skipIdentityRolloutGates);
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

    private static async Task<IdentityValidationOutcome> ValidateIdentityRolloutGatesAsync(
        ControlPlaneFleetClient controlPlane,
        LocalApiClient localApi,
        FleetValidationOptions options,
        MachineSummaryRow baselineSummary)
    {
        var gates = new List<IdentityGateResult>();

        LocalApiIdentityLinkResponse? initialLink = await localApi.GetIdentityLinkAsync().ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        LocalApiIdentityLinkResponse? secondLink = await localApi.GetIdentityLinkAsync().ConfigureAwait(false);

        string? deviceId = initialLink?.DeviceId ?? secondLink?.DeviceId;
        bool stableDeviceId =
            initialLink is not null
            && secondLink is not null
            && Guid.TryParse(initialLink.DeviceId, out _)
            && string.Equals(initialLink.DeviceId, secondLink.DeviceId, StringComparison.OrdinalIgnoreCase);

        string stableDeviceDetail = stableDeviceId
            ? $"Local API returned stable device_id={initialLink!.DeviceId} on repeated reads."
            : "Could not confirm a stable local device_id from /v1/identity/link.";
        gates.Add(CreateIdentityGate("stable_device_id", stableDeviceId, stableDeviceDetail));

        if (!stableDeviceId || initialLink is null || secondLink is null)
        {
            AddIdentityGateFailures(
                gates,
                "Stable local device_id could not be confirmed, so the identity rollout path was not exercised further.",
                "sign_in_links_device",
                "service_publishes_identity_refs",
                "control_plane_separation",
                "joined_endpoints_identity",
                "revoked_device_status",
                "unlinked_device_operational");
            return new IdentityValidationOutcome(gates, baselineSummary, deviceId, null, null);
        }

        string stableDeviceIdValue = initialLink.DeviceId;

        ControlPlaneLoginResponse? login =
            await controlPlane.LoginAsync(options.IdentityUserId!, options.IdentityPassword!, CancellationToken.None).ConfigureAwait(false);
        ControlPlaneMeProfile? me =
            login is not null
                ? await controlPlane.GetMeAsync(login.AccessToken, CancellationToken.None).ConfigureAwait(false)
                : null;
        ControlPlaneMembershipView? workspace = SelectWorkspace(me, options.IdentityOrgId);
        string? accountId = me?.Account.AccountId ?? login?.User.AccountId;
        string? orgId = workspace?.Membership.OrgId;

        if (login is null || me is null || string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(orgId) || workspace is null)
        {
            string detail =
                login is null
                    ? "Control plane login failed for the supplied identity credentials."
                    : me is null
                        ? "Control plane session was issued, but /v1/me did not return an account profile."
                        : options.IdentityOrgId is not null
                            ? $"Signed in, but workspace {options.IdentityOrgId} was not found in /v1/me."
                            : "Signed in, but no active workspace was available to link the device.";

            gates.Add(CreateIdentityGate("sign_in_links_device", false, detail));
            AddIdentityGateFailures(
                gates,
                $"Prerequisite failed: {detail}",
                "service_publishes_identity_refs",
                "control_plane_separation",
                "joined_endpoints_identity",
                "revoked_device_status",
                "unlinked_device_operational");
            return new IdentityValidationOutcome(gates, baselineSummary, deviceId, accountId, orgId);
        }

        if (IsDifferentIdentityLink(initialLink, accountId, orgId) && !options.AllowIdentityRelink)
        {
            string detail =
                $"Current device is already linked locally to account_id={initialLink.AccountId ?? "null"} org_id={initialLink.OrgId ?? "null"}. " +
                "Re-run with --allow-identity-relink to move this device for validation.";
            gates.Add(CreateIdentityGate("sign_in_links_device", false, detail));
            AddIdentityGateFailures(
                gates,
                $"Prerequisite failed: {detail}",
                "service_publishes_identity_refs",
                "control_plane_separation",
                "joined_endpoints_identity",
                "revoked_device_status",
                "unlinked_device_operational");
            return new IdentityValidationOutcome(gates, baselineSummary, deviceId, accountId, orgId);
        }

        LocalApiStatusResponse? localStatus = await localApi.GetStatusAsync().ConfigureAwait(false);
        LocalApiIdentityLinkResponse? linkedLocal =
            await localApi.SetIdentityLinkAsync(accountId, orgId).ConfigureAwait(false);

        DeviceRegistrationRequestRow currentDeviceRegistration = BuildDeviceRegistrationRequest(
            stableDeviceIdValue,
            options.MachineId,
            localStatus,
            accountId,
            orgId,
            login.AccessToken);

        DeviceLinkResultRow? initialRegistration =
            await controlPlane.RegisterDeviceAsync(currentDeviceRegistration, CancellationToken.None).ConfigureAwait(false);
        DeviceLinkResultRow? currentDeviceLink =
            await controlPlane.LinkDeviceAsync(
                new DeviceAccountLinkRequestRow(
                    DeviceId: stableDeviceIdValue,
                    AccountId: accountId,
                    OrgId: orgId,
                    SessionToken: login.AccessToken,
                    LocalProofToken: null),
                CancellationToken.None).ConfigureAwait(false);
        DeviceLinkResultRow? syncedRegistration =
            await controlPlane.RegisterDeviceAsync(currentDeviceRegistration, CancellationToken.None).ConfigureAwait(false);

        ControlPlaneDeviceProfile? currentSessionDevice = await WaitForSessionDeviceAsync(
            controlPlane,
            login.AccessToken,
            stableDeviceIdValue,
            DeviceInventoryTimeout,
            device =>
                string.Equals(device.CurrentAccountId, accountId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(device.OrgId, orgId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(device.Status, "unlinked", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(device.Status, "revoked", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);

        bool signInLinked =
            IsIdentityLinkedTo(linkedLocal, accountId, orgId)
            && currentSessionDevice is not null
            && string.Equals(currentSessionDevice.CurrentAccountId, accountId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(currentSessionDevice.OrgId, orgId, StringComparison.OrdinalIgnoreCase);

        string signInDetail = signInLinked
            ? $"Signed in as {me.Account.DisplayName} and linked current device_id={stableDeviceIdValue} to org_id={orgId}."
            : $"Local link={(linkedLocal?.LinkStatus ?? "missing")} register={(syncedRegistration ?? initialRegistration)?.LinkState ?? "missing"} current-device={(currentSessionDevice?.Status ?? "missing")}.";
        gates.Add(CreateIdentityGate("sign_in_links_device", signInLinked, signInDetail));

        MachineSummaryRow? linkedSummary = await WaitForConditionAsync(
            controlPlane,
            options.MachineId,
            IdentityLinkTimeout,
            summary =>
                string.Equals(summary.DeviceId, stableDeviceIdValue, StringComparison.OrdinalIgnoreCase)
                && string.Equals(summary.AccountId, accountId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(summary.OrgId, orgId, StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);

        bool servicePublishesIdentityRefs = linkedSummary is not null;
        string servicePublishDetail = servicePublishesIdentityRefs
            ? $"Machine summary reported device_id/account_id/org_id at {linkedSummary!.ReportedAtUtc}."
            : $"Machine summary for {options.MachineId} did not publish device_id/account_id/org_id within {IdentityLinkTimeout.TotalSeconds:F0}s.";
        gates.Add(CreateIdentityGate("service_publishes_identity_refs", servicePublishesIdentityRefs, servicePublishDetail));

        MachineSummaryRow? machineDetail = await WaitForMachineDetailAsync(
            controlPlane,
            options.MachineId,
            TimeSpan.FromSeconds(10),
            row =>
                string.Equals(row.DeviceId, stableDeviceIdValue, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.AccountId, accountId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.OrgId, orgId, StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);

        ControlPlaneMeProfile? refreshedMe =
            await controlPlane.GetMeAsync(login.AccessToken, CancellationToken.None).ConfigureAwait(false) ?? me;
        ControlPlaneDeviceProfile? currentDevice =
            currentSessionDevice
            ?? refreshedMe.Devices.FirstOrDefault(device => string.Equals(device.DeviceId, stableDeviceIdValue, StringComparison.OrdinalIgnoreCase));

        bool controlPlaneSeparation =
            currentDevice is not null
            && !string.IsNullOrWhiteSpace(refreshedMe.Account.DisplayName)
            && !string.IsNullOrWhiteSpace(refreshedMe.Account.PrimaryEmail)
            && machineDetail is not null
            && !string.IsNullOrWhiteSpace(machineDetail.MachineName)
            && !string.IsNullOrWhiteSpace(machineDetail.CaptureContext);
        string controlPlaneSeparationDetail = controlPlaneSeparation
            ? $"/v1/me exposed account/device state for {refreshedMe.Account.DisplayName} while /v1/machines/{options.MachineId} retained machine truth."
            : "Could not confirm separate account, device, and machine-truth views across /v1/me and /v1/machines/:machineId.";
        gates.Add(CreateIdentityGate("control_plane_separation", controlPlaneSeparation, controlPlaneSeparationDetail));

        string? expectedOrgName = workspace.Organization?.OrgName;
        bool joinedEndpointsIdentity =
            linkedSummary is not null
            && machineDetail is not null
            && string.Equals(linkedSummary.DisplayName, refreshedMe.Account.DisplayName, StringComparison.Ordinal)
            && string.Equals(linkedSummary.PrimaryEmail, refreshedMe.Account.PrimaryEmail, StringComparison.OrdinalIgnoreCase)
            && string.Equals(linkedSummary.OrgName, expectedOrgName, StringComparison.Ordinal)
            && string.Equals(machineDetail.DisplayName, refreshedMe.Account.DisplayName, StringComparison.Ordinal)
            && string.Equals(machineDetail.PrimaryEmail, refreshedMe.Account.PrimaryEmail, StringComparison.OrdinalIgnoreCase)
            && string.Equals(machineDetail.OrgName, expectedOrgName, StringComparison.Ordinal)
            && string.Equals(machineDetail.Role, workspace.Membership.Role, StringComparison.OrdinalIgnoreCase);
        string joinedEndpointsDetail = joinedEndpointsIdentity
            ? $"Joined fleet reads returned display_name={refreshedMe.Account.DisplayName}, primary_email={refreshedMe.Account.PrimaryEmail}, org_name={expectedOrgName ?? orgId}."
            : "Joined machine reads did not return the expected display name, primary email, org name, and role.";
        gates.Add(CreateIdentityGate("joined_endpoints_identity", joinedEndpointsIdentity, joinedEndpointsDetail));

        string syntheticDeviceId = Guid.NewGuid().ToString();
        DeviceRegistrationRequestRow syntheticRegistration = BuildDeviceRegistrationRequest(
            syntheticDeviceId,
            $"{options.MachineId}-validation-revoke",
            localStatus,
            accountId,
            orgId,
            login.AccessToken);
        DeviceLinkResultRow? syntheticRegistered =
            await controlPlane.RegisterDeviceAsync(syntheticRegistration, CancellationToken.None).ConfigureAwait(false);
        DeviceLinkResultRow? syntheticLinked =
            await controlPlane.LinkDeviceAsync(
                new DeviceAccountLinkRequestRow(
                    DeviceId: syntheticDeviceId,
                    AccountId: accountId,
                    OrgId: orgId,
                    SessionToken: login.AccessToken,
                    LocalProofToken: null),
                CancellationToken.None).ConfigureAwait(false);
        DeviceLinkResultRow? syntheticRevoked =
            syntheticLinked is not null
                ? await controlPlane.RevokeSessionDeviceAsync(login.AccessToken, syntheticDeviceId, CancellationToken.None).ConfigureAwait(false)
                : null;
        IReadOnlyList<ControlPlaneDeviceProfile> sessionDevices =
            await controlPlane.GetSessionDevicesAsync(login.AccessToken, CancellationToken.None).ConfigureAwait(false);
        ControlPlaneDeviceProfile? revokedDevice =
            sessionDevices.FirstOrDefault(device => string.Equals(device.DeviceId, syntheticDeviceId, StringComparison.OrdinalIgnoreCase));

        bool revokedDeviceStatus =
            syntheticRegistered is not null
            && syntheticLinked is not null
            && syntheticRevoked is not null
            && (revokedDevice is null
                || (string.Equals(revokedDevice.Status, "revoked", StringComparison.OrdinalIgnoreCase) && revokedDevice.RevokedAt.HasValue));
        string revokedDeviceDetail = revokedDeviceStatus
            ? revokedDevice is null
                ? $"Synthetic device_id={syntheticDeviceId} no longer appears in the signed-in device list after revoke."
                : $"Synthetic device_id={syntheticDeviceId} now reports status={revokedDevice.Status}."
            : "Synthetic device could not be registered, linked, and observed as revoked through /v1/me/devices.";
        gates.Add(CreateIdentityGate("revoked_device_status", revokedDeviceStatus, revokedDeviceDetail));

        MachineSummaryRow? latestSummary = machineDetail ?? linkedSummary ?? baselineSummary;
        bool unlinkedDeviceOperational = false;
        string unlinkedDeviceDetail;
        LocalApiIdentityLinkResponse? clearedLocal = await localApi.ClearIdentityLinkAsync().ConfigureAwait(false);
        if (clearedLocal is null || !string.Equals(clearedLocal.DeviceId, stableDeviceIdValue, StringComparison.OrdinalIgnoreCase))
        {
            unlinkedDeviceDetail = "Local device link could not be cleared, so the unlinked-device compatibility gate was not exercised.";
        }
        else
        {
            MachineSummaryRow? unlinkedSummary = await WaitForConditionAsync(
                controlPlane,
                options.MachineId,
                IdentityLinkTimeout,
                summary =>
                    string.Equals(summary.DeviceId, stableDeviceIdValue, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(summary.AccountId)
                    && string.IsNullOrWhiteSpace(summary.OrgId)
                    && string.IsNullOrWhiteSpace(summary.DisplayName)
                    && string.IsNullOrWhiteSpace(summary.PrimaryEmail)
                    && string.IsNullOrWhiteSpace(summary.OrgName)
                    && string.IsNullOrWhiteSpace(summary.Role)).ConfigureAwait(false);

            LocalApiIdentityLinkResponse? restoredLocal =
                await localApi.SetIdentityLinkAsync(accountId, orgId).ConfigureAwait(false);
            MachineSummaryRow? restoredSummary = await WaitForConditionAsync(
                controlPlane,
                options.MachineId,
                IdentityLinkTimeout,
                summary =>
                    string.Equals(summary.DeviceId, stableDeviceIdValue, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(summary.AccountId, accountId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(summary.OrgId, orgId, StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);

            unlinkedDeviceOperational =
                unlinkedSummary is not null
                && restoredSummary is not null
                && IsIdentityLinkedTo(restoredLocal, accountId, orgId)
                && !string.IsNullOrWhiteSpace(unlinkedSummary.MachineName)
                && !string.IsNullOrWhiteSpace(unlinkedSummary.CaptureContext);
            unlinkedDeviceDetail = unlinkedDeviceOperational
                ? $"Machine {options.MachineId} stayed operational with null joined identity while local link was cleared, then restored cleanly."
                : "Machine truth did not remain operationally visible with null joined identity during the temporary local unlink.";
            latestSummary = restoredSummary ?? unlinkedSummary ?? latestSummary;
        }
        gates.Add(CreateIdentityGate("unlinked_device_operational", unlinkedDeviceOperational, unlinkedDeviceDetail));

        return new IdentityValidationOutcome(gates, latestSummary, stableDeviceIdValue, accountId, orgId);
    }

    private static async Task<FleetValidationResult> PublishIdentityRolloutReportAsync(
        ControlPlaneFleetClient controlPlane,
        IdentityValidationOutcome outcome)
    {
        RolloutValidationReportPayload payload = new(
            SuiteName: IdentityRolloutSuiteName,
            MachineId: outcome.LatestSummary?.MachineId ?? Environment.MachineName,
            DeviceId: outcome.DeviceId,
            AccountId: outcome.AccountId,
            OrgId: outcome.OrgId,
            GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
            ExpiresAtUtc: DateTimeOffset.UtcNow.Add(ValidationReportExpiry).ToString("O"),
            Passed: outcome.Gates.All(gate => gate.Result.Success),
            Gates: outcome.Gates.Select(gate => gate.ToPayload()).ToArray());

        RolloutValidationReportPayload? published =
            await controlPlane.PublishRolloutValidationReportAsync(payload, CancellationToken.None).ConfigureAwait(false);
        RolloutValidationReportPayload? readBack =
            await controlPlane.GetRolloutValidationReportAsync(CancellationToken.None).ConfigureAwait(false);

        bool publishedOk =
            published is not null
            && readBack is not null
            && string.Equals(readBack.SuiteName, payload.SuiteName, StringComparison.Ordinal)
            && string.Equals(readBack.MachineId, payload.MachineId, StringComparison.OrdinalIgnoreCase)
            && readBack.Gates.Count == payload.Gates.Count
            && readBack.Passed == payload.Passed;

        return publishedOk
            ? Pass(
                "identity rollout report -> control plane stores validation gates",
                $"Published {payload.Gates.Count} gate results to /v1/rollout/validation-gates with passed={payload.Passed}.")
            : Fail(
                "identity rollout report -> control plane stores validation gates",
                "Control plane did not accept or return the rollout validation report. Ensure operator headers have operator/admin permissions.");
    }

    private static async Task<FleetValidationResult> ValidatePublishedRolloutGuardrailAsync(ControlPlaneFleetClient controlPlane)
    {
        ControlPlaneRolloutEvaluationPayload? evaluation =
            await controlPlane.GetRolloutEvaluationAsync(CancellationToken.None).ConfigureAwait(false);
        ControlPlaneRolloutGuardrail? guardrail =
            evaluation?.GuardrailEval.FirstOrDefault(item => string.Equals(item.GuardrailId, "VALIDATION_GATES", StringComparison.OrdinalIgnoreCase));

        bool guardrailHealthy = guardrail is not null && string.Equals(guardrail.Status, "PASS", StringComparison.OrdinalIgnoreCase);
        return guardrailHealthy
            ? Pass(
                "rollout evaluate -> validation guardrail is green",
                $"GET /v1/rollout/evaluate reported VALIDATION_GATES={guardrail!.Status}.")
            : Fail(
                "rollout evaluate -> validation guardrail is green",
                $"GET /v1/rollout/evaluate reported VALIDATION_GATES={guardrail?.Status ?? "missing"}.");
    }

    private static DeviceRegistrationRequestRow BuildDeviceRegistrationRequest(
        string deviceId,
        string machineName,
        LocalApiStatusResponse? localStatus,
        string accountId,
        string orgId,
        string accessToken)
    {
        bool consentAccepted = localStatus?.ConsentActive == true;
        return new DeviceRegistrationRequestRow(
            DeviceId: deviceId,
            MachineName: machineName,
            DeviceDisplayName: machineName,
            Platform: "windows",
            AgentVersion: localStatus?.AgentVersion ?? "unknown",
            AccountId: accountId,
            OrgId: orgId,
            DeviceCaptureConsentState: localStatus is null ? null : consentAccepted ? "accepted" : "revoked",
            DeviceCaptureConsentVersion: localStatus?.ConsentVersion,
            DeviceCaptureConsentedAt: localStatus?.ConsentTimestampUtc?.ToString("O"),
            DeviceCaptureTelemetryLevel: localStatus is null ? null : consentAccepted ? TelemetryLevelAccepted : TelemetryLevelRejected,
            SessionToken: accessToken,
            LocalProofToken: null);
    }

    private static ControlPlaneMembershipView? SelectWorkspace(ControlPlaneMeProfile? me, string? requestedOrgId)
    {
        if (me is null || me.Organizations.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requestedOrgId))
        {
            return me.Organizations.FirstOrDefault(workspace =>
                string.Equals(workspace.Membership.OrgId, requestedOrgId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(workspace.Membership.Status, "active", StringComparison.OrdinalIgnoreCase));
        }

        return me.Organizations.FirstOrDefault(workspace =>
            string.Equals(workspace.Membership.Status, "active", StringComparison.OrdinalIgnoreCase));
    }

    private static FleetValidationResult ValidatePrimaryStartupPath(MachineSummaryRow summary)
    {
        string startupSource = summary.UserAgentStartupSource?.Trim() ?? "(missing)";
        string startupState = summary.UserAgentStartupState?.Trim() ?? "(missing)";
        string launchReason = summary.UserAgentLastLaunchReason?.Trim() ?? "(missing)";
        string heartbeatAge = summary.UserAgentHeartbeatAgeSeconds.HasValue
            ? $"{summary.UserAgentHeartbeatAgeSeconds.Value:F1}s"
            : "missing";

        bool primaryLaunchConfirmed =
            summary.UserSessionConnected
            && !string.IsNullOrWhiteSpace(summary.UserAgentHeartbeatUtc)
            && summary.UserAgentHeartbeatAgeSeconds.HasValue
            && !string.IsNullOrWhiteSpace(summary.UserAgentStartupSource)
            && !string.Equals(summary.UserAgentStartupSource, "control-center-recovery", StringComparison.OrdinalIgnoreCase);

        return primaryLaunchConfirmed
            ? Pass(
                "install/login startup -> first heartbeat comes from a primary OS launch path",
                $"startupSource={startupSource}, startupState={startupState}, launchReason={launchReason}, heartbeatAge={heartbeatAge}.")
            : Fail(
                "install/login startup -> first heartbeat comes from a primary OS launch path",
                $"Expected a connected session with a fresh heartbeat started by Run key / Windows restart, but got startupSource={startupSource}, startupState={startupState}, launchReason={launchReason}, heartbeatAge={heartbeatAge}, userSessionConnected={summary.UserSessionConnected}.");
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> ValidateUserAgentDegradeAndRecoverAsync(
        ControlPlaneFleetClient controlPlane,
        FleetValidationOptions options,
        MachineSummaryRow baselineSummary)
    {
        var results = new List<FleetValidationResult>();
        Process[] running = Process.GetProcessesByName("Porta.UserAgent");
        if (running.Length == 0)
        {
            results.Add(Fail("kill Porta.UserAgent -> service watchdog relaunches interactive session agent", "Porta.UserAgent is not running, so process-control validation cannot stop it."));
            return results;
        }

        long baselineReportedAtMs = ParseTimestampUtc(baselineSummary.ReportedAtUtc);
        foreach (Process process in running)
        {
            process.Kill(entireProcessTree: false);
            process.WaitForExit(10_000);
            process.Dispose();
        }

        MachineSummaryRow? recovered = await WaitForConditionAsync(
            controlPlane,
            options.MachineId,
            UserAgentRecoverTimeout,
            summary =>
                ParseTimestampUtc(summary.ReportedAtUtc) > baselineReportedAtMs
                && summary.UserSessionConnected
                && string.Equals(summary.UserAgentStartupSource, "service-watchdog", StringComparison.OrdinalIgnoreCase)
                && string.Equals(summary.UserAgentLastLaunchReason, "missing_process", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);

        results.Add(recovered is not null
            ? Pass(
                "kill Porta.UserAgent -> service watchdog relaunches interactive session agent",
                $"startupSource={recovered.UserAgentStartupSource}, launchReason={recovered.UserAgentLastLaunchReason}, reportedAt={recovered.ReportedAtUtc}.")
            : Fail(
                "kill Porta.UserAgent -> service watchdog relaunches interactive session agent",
                "Machine summary never showed a service-watchdog recovery launch within 45s after killing Porta.UserAgent."));

        if (recovered is not null)
        {
            bool captureHealthy =
                string.Equals(recovered.CaptureContext, "UserSession", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(recovered.CaptureFailureReason)
                && recovered.UserAgentHeartbeatAgeSeconds.HasValue
                && recovered.UserAgentHeartbeatAgeSeconds.Value <= 15d;

            results.Add(captureHealthy
                ? Pass(
                    "kill Porta.UserAgent -> capture returns healthy without manual restart",
                    $"captureContext={recovered.CaptureContext}, heartbeatAge={recovered.UserAgentHeartbeatAgeSeconds:F1}s, captureFailure={recovered.CaptureFailureReason ?? "none"}.")
                : Fail(
                    "kill Porta.UserAgent -> capture returns healthy without manual restart",
                    $"Recovered summary was not fully healthy: captureContext={recovered.CaptureContext}, heartbeatAge={recovered.UserAgentHeartbeatAgeSeconds?.ToString("F1") ?? "missing"}s, captureFailure={recovered.CaptureFailureReason ?? "none"}."));
        }

        return results;
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> ValidateControlCenterClosedAsync(
        ControlPlaneFleetClient controlPlane,
        LocalApiClient localApi,
        string machineId)
    {
        var results = new List<FleetValidationResult>();
        Process[] running = Process.GetProcessesByName("Porta.ControlCenter");
        if (running.Length == 0)
        {
            results.Add(Info(
                "close Control Center -> capture stays healthy",
                "Skipped because Control Center was not running during validation."));
            return results;
        }

        foreach (Process process in running)
        {
            process.Kill(entireProcessTree: false);
            process.WaitForExit(10_000);
            process.Dispose();
        }

        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        LocalApiStatusResponse? localStatus = await localApi.GetStatusAsync().ConfigureAwait(false);
        MachineSummaryRow? summary = await WaitForConditionAsync(
            controlPlane,
            machineId,
            ControlCenterClosureTimeout,
            row =>
                row.UserSessionConnected
                && string.Equals(row.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase)
                && (!row.UserAgentHeartbeatAgeSeconds.HasValue || row.UserAgentHeartbeatAgeSeconds.Value <= 15d)).ConfigureAwait(false);

        bool localHealthy =
            localStatus is not null
            && localStatus.ServiceRunning
            && string.Equals(localStatus.CaptureContext, "UserSession", StringComparison.Ordinal)
            && localStatus.UserAgentHeartbeatUpdatedAtUtc.HasValue;

        results.Add(localHealthy && summary is not null
            ? Pass(
                "close Control Center -> capture stays healthy",
                $"localHeartbeat={localStatus!.user_agent_heartbeat_utc ?? "missing"}, fleetHeartbeatAge={summary.UserAgentHeartbeatAgeSeconds?.ToString("F1") ?? "missing"}s, captureContext={summary.CaptureContext}.")
            : Fail(
                "close Control Center -> capture stays healthy",
                $"localStatus={(localStatus is null ? "missing" : $"captureContext={localStatus.CaptureContext}, heartbeat={localStatus.user_agent_heartbeat_utc ?? "missing"}")}; fleetSummary={(summary is null ? "missing" : $"captureContext={summary.CaptureContext}, heartbeatAge={summary.UserAgentHeartbeatAgeSeconds?.ToString("F1") ?? "missing"}s, connected={summary.UserSessionConnected}")}."));

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
                summary.AgentRunning
                && summary.UserSessionConnected
                && summary.ActivityDetected
                && ParseTimestampUtc(summary.LastActivityUtc) > baselineActivityMs
                && (summary.KeyboardSignalActive || summary.MouseSignalActive)).ConfigureAwait(false);

        results.Add(updated is not null
            ? Pass(
                "active typing/mouse -> machine truth reaches Yes / Yes / Yes",
                $"agentRunning={updated.AgentRunning}, userSessionConnected={updated.UserSessionConnected}, activityDetected={updated.ActivityDetected}, lastActivityUtc={updated.LastActivityUtc}, startupState={updated.UserAgentStartupState ?? "(missing)"}.")
            : Fail(
                "active typing/mouse -> machine truth reaches Yes / Yes / Yes",
                "Machine summary did not show AgentRunning=true, UserSessionConnected=true, and ActivityDetected=true within 25s after the manual input window."));

        return results;
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> ValidateRejectedPacketOwnershipAsync(
        LocalApiClient localApi)
    {
        var results = new List<FleetValidationResult>();
        LocalApiStatusResponse? baseline = await localApi.GetStatusAsync().ConfigureAwait(false);
        if (baseline is null)
        {
            results.Add(Fail(
                "rejected input packet -> last accepted owner/session stays unchanged",
                "Local status could not be loaded before sending the rejected packet."));
            return results;
        }

        if (string.IsNullOrWhiteSpace(baseline.UserAgentInstanceId)
            || string.IsNullOrWhiteSpace(baseline.UserAgentSessionId)
            || string.IsNullOrWhiteSpace(baseline.LastInputAgentInstanceId)
            || string.IsNullOrWhiteSpace(baseline.LastInputSessionId))
        {
            results.Add(Fail(
                "rejected input packet -> last accepted owner/session stays unchanged",
                "Local status does not yet have a complete owner/session baseline. Generate real input first."));
            return results;
        }

        string mismatchedAgentInstanceId =
            !string.Equals(baseline.UserAgentInstanceId, "validation-bad-agent", StringComparison.Ordinal)
                ? "validation-bad-agent"
                : "validation-bad-agent-2";
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        LocalApiAckResponse? ack = await localApi.SendInputSignalsAsync(new LocalApiInputSignalsRequest(
            SchemaVer: 2,
            AgentInstanceId: mismatchedAgentInstanceId,
            SessionId: baseline.UserAgentSessionId,
            WindowStartUtc: nowUtc.AddSeconds(-1),
            WindowEndUtc: nowUtc,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0d,
            MouseMoveDy: 0d,
            FocusAppHash: null,
            IdleSeconds: 0d,
            LastInputEventUtc: nowUtc,
            KeyboardIkiMeanMs: 110d,
            KeyboardIkiStdMs: 12d,
            KeyboardIkiCv: 0.11d,
            KeyboardBurstCount: 1,
            KeyboardBurstRate: 1d,
            KeyboardCorrectionSpikeCount: 0,
            KeyboardCorrectionSpikeRate: 0d,
            MouseMoveSampleCount: 0,
            MousePathLengthPx: 0d,
            MouseSpeedMeanPxPerSec: 0d,
            MouseJerkP95: 0d,
            MouseMicroCorrectionCount: 0,
            MouseMicroCorrectionRate: 0d,
            MouseWheelEventCount: 0,
            MouseScrollBurstCount: 0,
            MouseScrollBurstRate: 0d,
            MouseScrollSpeedMean: 0d,
            FocusObservationCount: 1,
            FocusSwitchCount: 0,
            FocusActiveAppCount: 1,
            FocusAppEntropy: 0d,
            IdleObservationCount: 1,
            IdleSecondsWithinWindow: 0d,
            MicroIdleCount: 0,
            MicroIdleSeconds: 0d,
            BreakSecondsWithinWindow: 0d,
            ShortIdleBoundaryObserved: false,
            NotificationToastsEnabled: true)).ConfigureAwait(false);

        await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

        LocalApiStatusResponse? after = await localApi.GetStatusAsync().ConfigureAwait(false);
        bool rejectedAsMismatch =
            ack is not null
            && !ack.Success
            && string.Equals(ack.Message, "input_signals_source_agent_mismatch", StringComparison.OrdinalIgnoreCase);
        bool ownerPreserved =
            after is not null
            && string.Equals(after.LastInputAgentInstanceId, baseline.LastInputAgentInstanceId, StringComparison.Ordinal)
            && string.Equals(after.LastInputSessionId, baseline.LastInputSessionId, StringComparison.Ordinal)
            && string.Equals(after.OwnershipWarning, baseline.OwnershipWarning, StringComparison.Ordinal);

        results.Add(rejectedAsMismatch && ownerPreserved
            ? Pass(
                "rejected input packet -> last accepted owner/session stays unchanged",
                $"Rejected packet message={ack!.Message}; lastInputAgentInstanceId stayed {after!.LastInputAgentInstanceId}; lastInputSessionId stayed {after.LastInputSessionId}.")
            : Fail(
                "rejected input packet -> last accepted owner/session stays unchanged",
                $"ack={(ack is null ? "null" : $"{ack.Success}/{ack.Message}")}; baselineOwner={baseline.LastInputAgentInstanceId}/{baseline.LastInputSessionId}; afterOwner={after?.LastInputAgentInstanceId ?? "null"}/{after?.LastInputSessionId ?? "null"}; baselineWarning={baseline.OwnershipWarning ?? "null"}; afterWarning={after?.OwnershipWarning ?? "null"}."));

        return results;
    }

    private static async Task<IReadOnlyList<FleetValidationResult>> ValidateIdleConnectedAsync(
        ControlPlaneFleetClient controlPlane,
        string machineId,
        MachineSummaryRow baselineSummary)
    {
        var results = new List<FleetValidationResult>();

        Console.WriteLine();
        Console.WriteLine("Idle gate: do not touch the keyboard or mouse for 20 seconds. Validation will continue automatically...");
        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        long baselineReportedAtMs = ParseTimestampUtc(baselineSummary.ReportedAtUtc);
        MachineSummaryRow? idle = await WaitForConditionAsync(
            controlPlane,
            machineId,
            IdleTimeout,
            summary =>
                ParseTimestampUtc(summary.ReportedAtUtc) > baselineReportedAtMs
                && summary.AgentRunning
                && summary.UserSessionConnected
                && !summary.ActivityDetected
                && string.Equals(summary.UserAgentStartupState, "idle", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);

        results.Add(idle is not null
            ? Pass(
                "idle logged-in machine -> machine truth stays Yes / Yes / No",
                $"agentRunning={idle.AgentRunning}, userSessionConnected={idle.UserSessionConnected}, activityDetected={idle.ActivityDetected}, startupState={idle.UserAgentStartupState}, lastAcceptedInputAgeSeconds={idle.LastAcceptedInputAgeSeconds?.ToString("F1") ?? "missing"}.")
            : Fail(
                "idle logged-in machine -> machine truth stays Yes / Yes / No",
                $"Machine summary did not settle to AgentRunning=true, UserSessionConnected=true, ActivityDetected=false within {IdleTimeout.TotalSeconds:F0}s after the idle window."));

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

        double localPressureLevel = localSnapshot.PressureLevel ?? localSnapshot.OverloadLikelihood;
        if (Math.Abs(summary.PressureLevel - localPressureLevel) > 0.05d)
        {
            mismatches.Add($"pressureLevel local={localPressureLevel:F3} fleet={summary.PressureLevel:F3}");
        }

        if (Math.Abs(summary.OverloadLikelihood - summary.PressureLevel) > 0.0001d)
        {
            mismatches.Add($"overloadLikelihood compatibility drift fleet_pressure={summary.PressureLevel:F3} fleet_legacy={summary.OverloadLikelihood:F3}");
        }

        if (Math.Abs(summary.FocusEpisodeSeconds - localSnapshot.FocusEpisodeSeconds) > 60d)
        {
            mismatches.Add($"focusEpisodeSeconds local={localSnapshot.FocusEpisodeSeconds:F0} fleet={summary.FocusEpisodeSeconds:F0}");
        }

        if (Math.Abs(summary.TaskContinuitySeconds - summary.FocusEpisodeSeconds) > 0.0001d)
        {
            mismatches.Add($"taskContinuitySeconds compatibility drift fleet_focusEpisode={summary.FocusEpisodeSeconds:F0} fleet_legacy={summary.TaskContinuitySeconds:F0}");
        }

        if (!string.Equals(summary.BreakPendingKind, localSnapshot.BreakPendingKind, StringComparison.Ordinal))
        {
            mismatches.Add($"breakPendingKind local={localSnapshot.BreakPendingKind ?? "null"} fleet={summary.BreakPendingKind ?? "null"}");
        }

        double localModelConfidence = localSnapshot.ModelConfidence ?? localSnapshot.Confidence;
        if (Math.Abs(summary.ModelConfidence - localModelConfidence) > 0.08d)
        {
            mismatches.Add($"modelConfidence local={localModelConfidence:F3} fleet={summary.ModelConfidence:F3}");
        }

        results.Add(mismatches.Count == 0
            ? Pass(
                "ready/high-trust session -> control plane truth matches local Control Center truth",
                $"calibration={summary.CalibrationStage}, trust={summary.TrustTier}, focusEpisode={summary.FocusEpisodeSeconds:F0}s, pressure={summary.PressureLevel:F2}.")
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

    private static async Task<MachineSummaryRow?> WaitForMachineDetailAsync(
        ControlPlaneFleetClient client,
        string machineId,
        TimeSpan timeout,
        Func<MachineSummaryRow, bool> predicate)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            MachineSummaryRow? row = await client.GetMachineSummaryAsync(machineId, timeoutCts.Token).ConfigureAwait(false);
            if (row is not null && predicate(row))
            {
                return row;
            }

            await Task.Delay(SummaryPollInterval, timeoutCts.Token).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<ControlPlaneDeviceProfile?> WaitForSessionDeviceAsync(
        ControlPlaneFleetClient client,
        string accessToken,
        string deviceId,
        TimeSpan timeout,
        Func<ControlPlaneDeviceProfile, bool> predicate)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            IReadOnlyList<ControlPlaneDeviceProfile> devices =
                await client.GetSessionDevicesAsync(accessToken, timeoutCts.Token).ConfigureAwait(false);
            ControlPlaneDeviceProfile? match = devices.FirstOrDefault(device =>
                string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (match is not null && predicate(match))
            {
                return match;
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

    private static bool IsIdentityLinked(LocalApiIdentityLinkResponse? link)
        => link is not null
           && string.Equals(link.LinkStatus, "linked", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(link.AccountId)
           && !string.IsNullOrWhiteSpace(link.OrgId);

    private static bool IsIdentityLinkedTo(LocalApiIdentityLinkResponse? link, string? accountId, string? orgId)
        => IsIdentityLinked(link)
           && string.Equals(link!.AccountId, accountId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(link.OrgId, orgId, StringComparison.OrdinalIgnoreCase);

    private static bool IsDifferentIdentityLink(LocalApiIdentityLinkResponse link, string accountId, string orgId)
        => IsIdentityLinked(link)
           && (!string.Equals(link.AccountId, accountId, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(link.OrgId, orgId, StringComparison.OrdinalIgnoreCase));

    private static string IdentityGateName(string gateId)
        => gateId switch
        {
            "stable_device_id" => "new install -> stable local device_id exists",
            "sign_in_links_device" => "sign-in links current device to account/org",
            "service_publishes_identity_refs" => "service publishes device_id/account_id/org_id",
            "control_plane_separation" => "control plane keeps profile, device, and telemetry views separate",
            "joined_endpoints_identity" => "joined endpoints show real name/email/org",
            "revoked_device_status" => "revoked device disappears or shows revoked status correctly",
            "unlinked_device_operational" => "unlinked device still appears operationally without fake identity",
            _ => gateId
        };

    private static IdentityGateResult CreateIdentityGate(string gateId, bool success, string detail)
        => new(
            GateId: gateId,
            Result: success ? Pass(IdentityGateName(gateId), detail) : Fail(IdentityGateName(gateId), detail),
            CheckedAtUtc: DateTimeOffset.UtcNow.ToString("O"));

    private static void AddIdentityGateFailures(
        List<IdentityGateResult> gates,
        string detail,
        params string[] gateIds)
    {
        foreach (string gateId in gateIds)
        {
            gates.Add(CreateIdentityGate(gateId, false, detail));
        }
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
