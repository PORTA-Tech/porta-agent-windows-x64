using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Porta.Agent.LocalApi;
using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter;

public partial class MainWindow
{
    private static readonly TimeSpan AccountRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly ControlPlaneAccountClient _controlPlaneAccountClient = new();
    private readonly ControlPlaneAccountSessionStore _accountSessionStore = new();
    private readonly ControlPlaneRememberedSignInStore _rememberedSignInStore = new();
    private readonly ObservableCollection<AccountWorkspaceOption> _accountWorkspaceOptions = [];
    private readonly ObservableCollection<AccountDeviceRowView> _accountDeviceRows = [];
    private LocalIdentityLinkCoordinator _identityLinkCoordinator = null!;
    private ControlPlaneSessionState? _accountSession;
    private RememberedSignInState? _rememberedSignIn;
    private ControlPlaneMeProfileView? _accountProfile;
    private ControlPlaneAuthAccessHelpResponse? _accountAccessHelp;
    private LocalApiIdentityLinkResponse? _latestIdentityLink;
    private DateTimeOffset _lastAccountRefreshUtc = DateTimeOffset.MinValue;
    private AccountSurfaceSection _activeAccountSection = AccountSurfaceSection.Summary;
    private string _accountStatusMessage = "Sign in to view your account, workspace, and devices.";

    private enum AccountSurfaceSection
    {
        Summary,
        ManageAccount,
        Workspace,
        Subscription,
        Devices,
        Privacy,
        SignOut
    }

    private void InitializeAccountSurface()
    {
        _identityLinkCoordinator = new LocalIdentityLinkCoordinator(_apiClient);
        _accountSession = _accountSessionStore.Load();
        _rememberedSignIn = _rememberedSignInStore.Load();

        if (AccountWorkspaceComboBox != null)
        {
            AccountWorkspaceComboBox.ItemsSource = _accountWorkspaceOptions;
            AccountWorkspaceComboBox.DisplayMemberPath = nameof(AccountWorkspaceOption.Label);
        }

        if (AccountDevicesItemsControl != null)
        {
            AccountDevicesItemsControl.ItemsSource = _accountDeviceRows;
        }

        AccountRememberSignInCheckBox.IsChecked = _rememberedSignIn != null || AccountRememberSignInCheckBox.IsChecked == true;
        RestoreRememberedSignInInputs(force: true);
        ShowAccountSection(_activeAccountSection);
        PopulateAccountSurface();
    }

    private async Task RefreshAccountSurfaceAsync(bool force = false)
    {
        _latestIdentityLink = await _identityLinkCoordinator.GetCurrentAsync().ConfigureAwait(true);
        await RefreshWindowsReleaseAsync(force).ConfigureAwait(true);

        bool shouldRefreshAccessHelp = force || (_activeSection == UiSection.Account && _accountAccessHelp == null);
        if (shouldRefreshAccessHelp)
        {
            ControlPlaneAuthAccessHelpResponse? accessHelp = await _controlPlaneAccountClient.GetAccessHelpAsync().ConfigureAwait(true);
            if (accessHelp != null)
            {
                _accountAccessHelp = accessHelp;
            }
        }

        bool shouldRefreshRemote = force
            || _activeSection == UiSection.Account
            || (_accountSession != null && (DateTimeOffset.UtcNow - _lastAccountRefreshUtc) >= AccountRefreshInterval);

        if (_accountSession != null && shouldRefreshRemote)
        {
            ControlPlaneDeviceCallResult deviceRegistration = await RegisterCurrentDeviceAsync().ConfigureAwait(true);
            string? remoteDeviceStatusMessage = null;
            if (ShouldClearLocalIdentityLink(deviceRegistration.Result))
            {
                LocalApiIdentityLinkResponse? local = await _identityLinkCoordinator.ClearAsync().ConfigureAwait(true);
                if (local != null)
                {
                    _latestIdentityLink = local;
                }

                remoteDeviceStatusMessage = BuildRemoteDeviceStatusMessage(deviceRegistration.Result!);
            }

            ControlPlaneMeProfileView? me = await _controlPlaneAccountClient.GetMeAsync(_accountSession.AccessToken).ConfigureAwait(true);
            if (me == null)
            {
                _accountSessionStore.Clear();
                _accountSession = null;
                _accountProfile = null;
                _accountWorkspaceOptions.Clear();
                _lastAccountRefreshUtc = DateTimeOffset.MinValue;
                _accountStatusMessage = "Your sign-in expired. Sign in again to view your account.";
            }
            else
            {
                _accountProfile = me;
                SyncWorkspaceOptions();

                // Devices linked before this shipped hold no credential. This is how they get
                // one -- no unlink/relink dance, which would never reach most of the fleet.
                await EnsureDeviceCredentialAsync().ConfigureAwait(true);

                string? autoLinkedWorkspaceName = await TryAutoLinkCurrentDeviceAsync().ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(autoLinkedWorkspaceName))
                {
                    ControlPlaneMeProfileView? refreshedMe = await _controlPlaneAccountClient.GetMeAsync(_accountSession.AccessToken).ConfigureAwait(true);
                    if (refreshedMe != null)
                    {
                        _accountProfile = refreshedMe;
                        SyncWorkspaceOptions();
                    }
                }

                _lastAccountRefreshUtc = DateTimeOffset.UtcNow;
                _accountStatusMessage = remoteDeviceStatusMessage
                    ?? (!string.IsNullOrWhiteSpace(autoLinkedWorkspaceName)
                        ? $"Signed in. This PC is now linked to {autoLinkedWorkspaceName}."
                        : "Signed in. Use the menu to open account, workspace, devices, or privacy.");
            }
        }

        PopulateAccountSurface();
    }

    private async Task<ControlPlaneDeviceCallResult> RegisterCurrentDeviceAsync()
    {
        if (_accountSession == null || _latestIdentityLink == null)
        {
            return new ControlPlaneDeviceCallResult(ControlPlaneCallOutcome.Rejected, null);
        }

        string? linkedAccountId = IsIdentityLinkedToSignedInAccount(_latestIdentityLink) ? _latestIdentityLink!.AccountId : null;
        string? linkedOrgId = IsIdentityLinkedToSignedInAccount(_latestIdentityLink) ? _latestIdentityLink!.OrgId : null;

        var request = new ControlPlaneDeviceRegistrationRequest(
            DeviceId: _latestIdentityLink.DeviceId,
            MachineName: Environment.MachineName,
            DeviceDisplayName: Environment.MachineName,
            Platform: "windows",
            AgentVersion: _latestStatus?.AgentVersion ?? _uiVersion,
            AccountId: linkedAccountId,
            OrgId: linkedOrgId,
            DeviceCaptureConsentState: _latestStatus != null
                ? (_latestStatus.ConsentActive ? "accepted" : "revoked")
                : null,
            DeviceCaptureConsentVersion: _latestStatus?.ConsentVersion,
            DeviceCaptureConsentedAt: _latestStatus?.ConsentTimestampUtc?.ToString("O"),
            DeviceCaptureTelemetryLevel: null,
            SessionToken: _accountSession.AccessToken,
            LocalProofToken: null);

        return await _controlPlaneAccountClient.RegisterDeviceAsync(request, _accountSession.AccessToken).ConfigureAwait(true);
    }

    private void SyncWorkspaceOptions()
    {
        string? selectedOrgId = (AccountWorkspaceComboBox.SelectedItem as AccountWorkspaceOption)?.OrgId;
        _accountWorkspaceOptions.Clear();

        if (_accountProfile?.Organizations == null)
        {
            AccountWorkspaceComboBox.SelectedItem = null;
            return;
        }

        foreach (ControlPlaneAccountMembershipView membershipView in _accountProfile.Organizations)
        {
            if (membershipView.Organization == null
                || !string.Equals(membershipView.Membership.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _accountWorkspaceOptions.Add(new AccountWorkspaceOption(
                membershipView.Organization.OrgId,
                membershipView.Organization.OrgName,
                membershipView.Membership.Role,
                membershipView.Organization.PlanTier));
        }

        AccountWorkspaceOption? preferred = !string.IsNullOrWhiteSpace(selectedOrgId)
            ? _accountWorkspaceOptions.FirstOrDefault(option => string.Equals(option.OrgId, selectedOrgId, StringComparison.OrdinalIgnoreCase))
            : null;
        preferred ??= ResolvePreferredWorkspace();

        if (preferred != null)
        {
            AccountWorkspaceComboBox.SelectedItem = _accountWorkspaceOptions
                .FirstOrDefault(option => string.Equals(option.OrgId, preferred.OrgId, StringComparison.OrdinalIgnoreCase));
        }
        else if (_accountWorkspaceOptions.Count > 0)
        {
            AccountWorkspaceComboBox.SelectedIndex = 0;
        }
        else
        {
            AccountWorkspaceComboBox.SelectedItem = null;
        }
    }

    private AccountWorkspaceOption? ResolvePreferredWorkspace()
    {
        if (_accountWorkspaceOptions.Count == 0)
        {
            return null;
        }

        if (_latestIdentityLink != null
            && IsIdentityLinkedToSignedInAccount(_latestIdentityLink)
            && !string.IsNullOrWhiteSpace(_latestIdentityLink.OrgId))
        {
            AccountWorkspaceOption? matching = _accountWorkspaceOptions
                .FirstOrDefault(option => string.Equals(option.OrgId, _latestIdentityLink.OrgId, StringComparison.OrdinalIgnoreCase));
            if (matching != null)
            {
                return matching;
            }
        }

        return _accountWorkspaceOptions[0];
    }

    private bool NeedsWorkspaceLink(AccountWorkspaceOption workspace)
    {
        if (_accountSession == null || _latestIdentityLink == null)
        {
            return false;
        }

        return !IsIdentityLinked(_latestIdentityLink)
            || !string.Equals(_latestIdentityLink.AccountId, _accountSession.AccountId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_latestIdentityLink.OrgId, workspace.OrgId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> LinkCurrentDeviceToWorkspaceAsync(AccountWorkspaceOption workspace, bool updateStatusMessage = true)
    {
        if (_accountSession == null || _latestIdentityLink == null)
        {
            return false;
        }

        ControlPlaneDeviceCallResult remote = await _controlPlaneAccountClient.LinkDeviceAsync(
            new ControlPlaneDeviceAccountLinkRequest(
                DeviceId: _latestIdentityLink.DeviceId,
                AccountId: _accountSession.AccountId,
                OrgId: workspace.OrgId,
                SessionToken: _accountSession.AccessToken,
                LocalProofToken: null),
            _accountSession.AccessToken).ConfigureAwait(true);

        if (!remote.Ok)
        {
            // An expired sign-in is reported even when the caller asked to stay quiet:
            // auto-link runs silently, and staying silent here is what made an expired
            // session look like the feature was simply broken.
            if (remote.SessionRejected)
            {
                HandleSessionRejected(DeviceLinkOperation.LinkCurrentDevice);
                return false;
            }

            if (updateStatusMessage)
            {
                _accountStatusMessage = DeviceLinkStatusCopy.BuildFailureMessage(
                    DeviceLinkOperation.LinkCurrentDevice,
                    remote.Outcome);
                PopulateAccountSurface();
            }

            return false;
        }

        LocalApiIdentityLinkResponse? local = await _identityLinkCoordinator
            .LinkSignedInAccountAsync(_accountSession.AccountId, workspace.OrgId)
            .ConfigureAwait(true);

        if (local == null)
        {
            ControlPlaneDeviceCallResult rollback = await _controlPlaneAccountClient.UnlinkDeviceAsync(
                new ControlPlaneDeviceAccountUnlinkRequest(
                    DeviceId: _latestIdentityLink.DeviceId,
                    AccountId: _accountSession.AccountId,
                    OrgId: workspace.OrgId,
                    SessionToken: _accountSession.AccessToken,
                    LocalProofToken: null),
                _accountSession.AccessToken).ConfigureAwait(true);

            if (updateStatusMessage)
            {
                // The rollback can fail too, and then the control plane still has this
                // device linked while the PC does not. Say so rather than claim a clean stop.
                _accountStatusMessage = rollback.Ok
                    ? "Could not finish linking this PC to the selected workspace."
                    : "Could not finish linking this PC, and it is still linked to the workspace on the server. Try again.";
                PopulateAccountSurface();
            }

            return false;
        }

        _latestIdentityLink = local;
        await RegisterCurrentDeviceAsync().ConfigureAwait(true);
        await EnsureDeviceCredentialAsync().ConfigureAwait(true);

        if (updateStatusMessage)
        {
            _accountStatusMessage = $"Current device linked to {workspace.OrgName}.";
        }

        return true;
    }

    /// <summary>
    /// Makes sure the service holds a device credential for this machine, issuing one if not.
    ///
    /// Runs on two paths: immediately after a link, and on every account refresh. The refresh
    /// path is the one that matters for machines linked before device credentials existed --
    /// there is no autostart for the Control Center, so anything requiring the user to
    /// deliberately unlink and relink would never reach most of the fleet.
    ///
    /// Deliberately quiet: a failure here costs nothing a user can see. The device keeps
    /// reporting as anonymous inventory and the next refresh tries again.
    /// </summary>
    private async Task EnsureDeviceCredentialAsync()
    {
        if (_accountSession == null || _latestIdentityLink == null)
        {
            return;
        }

        if (!IsIdentityLinkedToSignedInAccount(_latestIdentityLink))
        {
            return;
        }

        if (await _identityLinkCoordinator.HasDeviceCredentialAsync().ConfigureAwait(true))
        {
            return;
        }

        ControlPlaneDeviceCredentialResponse? issued = await _controlPlaneAccountClient
            .IssueDeviceCredentialAsync(_latestIdentityLink.DeviceId, _accountSession.AccessToken)
            .ConfigureAwait(true);

        if (issued == null || !issued.Ok || string.IsNullOrWhiteSpace(issued.DeviceSecret))
        {
            return;
        }

        // Straight to the service, which owns the secrets directory. The plaintext is returned
        // exactly once, so it is never written anywhere the Control Center controls.
        await _identityLinkCoordinator
            .StoreDeviceCredentialAsync(issued.DeviceId, issued.DeviceSecret)
            .ConfigureAwait(true);
    }

    private async Task<string?> TryAutoLinkCurrentDeviceAsync()
    {
        if (_accountSession == null || _latestIdentityLink == null)
        {
            return null;
        }

        if (IsIdentityLinkedToDifferentAccount(_latestIdentityLink))
        {
            return null;
        }

        AccountWorkspaceOption? workspace = ResolvePreferredWorkspace();
        if (workspace == null || !NeedsWorkspaceLink(workspace))
        {
            return null;
        }

        bool linked = await LinkCurrentDeviceToWorkspaceAsync(workspace, updateStatusMessage: false).ConfigureAwait(true);
        return linked ? workspace.OrgName : null;
    }

    private async Task<bool> UnlinkCurrentDeviceAsync(bool updateStatusMessage)
    {
        if (_latestIdentityLink == null)
        {
            return false;
        }

        if (_accountSession != null && IsIdentityLinked(_latestIdentityLink))
        {
            ControlPlaneDeviceCallResult remote = await _controlPlaneAccountClient.UnlinkDeviceAsync(
                new ControlPlaneDeviceAccountUnlinkRequest(
                    DeviceId: _latestIdentityLink.DeviceId,
                    AccountId: _latestIdentityLink.AccountId,
                    OrgId: _latestIdentityLink.OrgId,
                    SessionToken: _accountSession.AccessToken,
                    LocalProofToken: null),
                _accountSession.AccessToken).ConfigureAwait(true);

            // This result used to be discarded. Clearing the local link after a failed
            // remote unlink leaves the PC saying "unlinked" while the control plane still
            // has the device linked, and nothing ever reconciles the two.
            if (!remote.Ok)
            {
                if (remote.SessionRejected)
                {
                    HandleSessionRejected(DeviceLinkOperation.UnlinkCurrentDevice);
                    return false;
                }

                if (updateStatusMessage)
                {
                    _accountStatusMessage = DeviceLinkStatusCopy.BuildFailureMessage(
                        DeviceLinkOperation.UnlinkCurrentDevice,
                        remote.Outcome);
                    PopulateAccountSurface();
                }

                return false;
            }
        }

        LocalApiIdentityLinkResponse? local = await _identityLinkCoordinator.ClearAsync().ConfigureAwait(true);
        if (local != null)
        {
            _latestIdentityLink = local;
        }

        if (updateStatusMessage)
        {
            _accountStatusMessage = "This device is no longer linked to an account workspace.";
        }

        return true;
    }

    /// <summary>
    /// The control plane rejected the stored session. Drop it and tell the user to sign in
    /// again -- this mirrors what the /v1/me refresh path already does, so an expired session
    /// looks the same wherever it is discovered. Sessions last 30 days; everyone hits this.
    /// </summary>
    private void HandleSessionRejected(DeviceLinkOperation operation)
    {
        _accountSessionStore.Clear();
        _accountSession = null;
        _accountProfile = null;
        _accountWorkspaceOptions.Clear();
        _accountDeviceRows.Clear();
        _lastAccountRefreshUtc = DateTimeOffset.MinValue;
        _activeAccountSection = AccountSurfaceSection.Summary;
        _accountStatusMessage = DeviceLinkStatusCopy.BuildFailureMessage(
            operation,
            ControlPlaneCallOutcome.Unauthorized);

        RestoreRememberedSignInInputs(force: true);
        PopulateAccountSurface();
    }

    private void PopulateAccountSurface()
    {
        AccountStatusText.Text = _accountStatusMessage;
        PopulateUpdateSurface();

        bool signedIn = _accountSession != null && _accountProfile != null;
        bool backendConfigured = !string.IsNullOrWhiteSpace(_controlPlaneAccountClient.GetConfiguredBaseUrl());
        AccountSignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountSignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        // Profile card: show when signed in
        AccountProfileCard.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (signedIn && _accountProfile?.Account is { } prof)
        {
            string displayName = !string.IsNullOrWhiteSpace(prof.DisplayName) ? prof.DisplayName : prof.PrimaryEmail ?? "Account";
            AccountProfileNameText.Text = displayName;
            AccountProfileEmailText.Text = prof.PrimaryEmail ?? string.Empty;
            AccountAvatarInitialText.Text = displayName.Length > 0
                ? displayName[0].ToString().ToUpperInvariant()
                : "A";
            // Device: use first device from profile, or the linked device name
            ControlPlaneDeviceProfile? thisDevice = _accountProfile?.Devices.FirstOrDefault();
            AccountProfileDeviceText.Text = thisDevice?.DeviceDisplayName ?? thisDevice?.MachineName ?? "â€”";
            // Workspace: first org name
            ControlPlaneAccountMembershipView? firstOrg = _accountProfile?.Organizations.FirstOrDefault();
            AccountProfileWorkspaceText.Text = firstOrg?.Organization?.OrgName ?? "â€”";
            // Session ID: abbreviate
            AccountProfileSessionText.Text = _accountSession?.AccountId is { Length: > 0 } aid
                ? (aid.Length > 14 ? aid[..14] + "â€¦" : aid)
                : "â€”";
        }

        AccountSignInButton.IsEnabled = !signedIn && backendConfigured;
        AccountRefreshButton.IsEnabled = signedIn;

        ControlPlaneAccountProfile? account = _accountProfile?.Account;
        AccountSignedInAsValue.Text = account?.DisplayName ?? "Not signed in";
        AccountEmailValue.Text = account?.PrimaryEmail ?? NotAvailableText;
        AccountSessionExpiryValue.Text = _accountSession != null
            ? FormatSessionExpiry(_accountSession.SessionExpiresAtUtc)
            : NotAvailableText;
        AccountSignedInSummaryText.Text = signedIn
            ? "Use the menu below to open your profile, workspace, subscription, devices, or privacy."
            : "Sign in to open your profile, workspace, subscription, devices, and privacy.";
        AccountUserIdHelpText.Text = BuildUserIdHelpText();
        AccountPasswordHelpText.Text = BuildPasswordHelpText();
        AccountRememberedSignInHelpText.Text = BuildRememberedSignInHelpText(signedIn);
        AccountCreateHelpText.Text = BuildCreateAccountHelpText(backendConfigured);
        AccountCreateDisplayNameHelpText.Text = BuildCreateDisplayNameHelpText();
        AccountCreateUserIdHelpText.Text = BuildCreateUserIdHelpText();
        AccountCreatePasswordHelpText.Text = BuildCreatePasswordHelpText();
        AccountSignupCodeHelpText.Text = BuildSignupCodeHelpText();
        AccountRegistrationPanel.Visibility = _accountAccessHelp?.SignupEnabled == true ? Visibility.Visible : Visibility.Collapsed;
        AccountSignupCodePanel.Visibility = _accountAccessHelp?.SignupCodeRequired == true ? Visibility.Visible : Visibility.Collapsed;
        AccountCreateAccountButton.IsEnabled = !signedIn && backendConfigured && _accountAccessHelp?.SignupEnabled == true;
        AccountRememberSignInCheckBox.IsEnabled = !signedIn && backendConfigured;
        AccountForgetRememberedSignInButton.Visibility = !signedIn && _rememberedSignIn != null
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!AccountDisplayNameTextBox.IsKeyboardFocusWithin)
        {
            AccountDisplayNameTextBox.Text = account?.DisplayName ?? string.Empty;
        }
        if (!AccountNotificationEmailTextBox.IsKeyboardFocusWithin)
        {
            AccountNotificationEmailTextBox.Text = account?.PrimaryEmail ?? string.Empty;
        }
        AccountNotificationEmailTextBox.IsEnabled = signedIn;
        AccountReadOnlyEmailValue.Text = account?.PrimaryEmail ?? NotAvailableText;
        AccountProfileStatusText.Text = signedIn
            ? "Change the name PORTA shows for your account."
            : "Sign in to change your display name.";
        AccountSaveProfileButton.IsEnabled = signedIn;

        AccountWorkspaceOption? selectedWorkspace = AccountWorkspaceComboBox.SelectedItem as AccountWorkspaceOption
            ?? ResolvePreferredWorkspace();
        AccountWorkspaceComboBox.IsEnabled = signedIn && _accountWorkspaceOptions.Count > 0;
        AccountLinkWorkspaceButton.IsEnabled = signedIn
            && _latestIdentityLink != null
            && selectedWorkspace != null
            && NeedsWorkspaceLink(selectedWorkspace);
        // Show info box when signed in but no workspace is linked yet
        AccountWorkspaceInfoBox.Visibility = (signedIn && _accountWorkspaceOptions.Count == 0)
            ? Visibility.Visible
            : Visibility.Collapsed;

        AccountWorkspaceNameValue.Text = ResolveWorkspaceDisplayName(selectedWorkspace);
        AccountWorkspaceRoleValue.Text = selectedWorkspace != null ? ToTitleCase(selectedWorkspace.Role) : NotAvailableText;
        AccountWorkspaceLinkValue.Text = BuildWorkspaceLinkStatus(selectedWorkspace);

        AccountPlanTierValue.Text = selectedWorkspace != null ? ToTitleCase(selectedWorkspace.PlanTier) : NotAvailableText;
        AccountSubscriptionStatusValue.Text = selectedWorkspace != null
            ? $"Plan for {selectedWorkspace.OrgName}."
            : (_latestIdentityLink != null && IsIdentityLinked(_latestIdentityLink))
                ? "Sign in to see plan details for this workspace."
                : signedIn
                    ? "Choose a workspace to see its plan."
                    : "Sign in to see plan details.";

        ControlPlaneDeviceProfile? currentDevice = ResolveCurrentDevice();
        ControlPlaneConsentRecord? accountLegalConsent = ResolveAccountLegalConsent();
        ControlPlaneConsentRecord? deviceCaptureConsent = ResolveCurrentDeviceCaptureConsent();
        AccountCurrentDeviceNameValue.Text = currentDevice?.DeviceDisplayName ?? Environment.MachineName;
        AccountCurrentDeviceOsValue.Text = Environment.OSVersion.VersionString;
        AccountCurrentDeviceVersionValue.Text = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "Unknown";
        AccountCurrentDeviceIdValue.Text = _latestIdentityLink?.DeviceId ?? NotAvailableText;
        AccountCurrentDeviceStatusValue.Text = currentDevice != null
            ? ToTitleCase(currentDevice.Status)
            : (_latestIdentityLink != null ? ToTitleCase(_latestIdentityLink.LinkStatus) : NotAvailableText);
        AccountCurrentDeviceLastSeenValue.Text = currentDevice != null
            ? FormatHeartbeatLabel(currentDevice.LastSeenAt)
            : (_latestIdentityLink != null ? FormatHeartbeatLabel(_latestIdentityLink.UpdatedAtUtc) : NotAvailableText);
        SyncAccountDeviceRows(signedIn);
        if (AccountDevicesEmptyText != null)
        {
            AccountDevicesEmptyText.Text = signedIn
                ? "No devices are linked to this account right now."
                : "Sign in to view this account's devices.";
            AccountDevicesEmptyText.Visibility = _accountDeviceRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        AccountConsentStatusValue.Text = accountLegalConsent != null
            ? ToTitleCase(accountLegalConsent.ConsentState)
            : (signedIn ? "Accepted" : NotAvailableText);
        AccountConsentVersionValue.Text = accountLegalConsent?.ConsentVersion ?? (signedIn ? "v1" : NotAvailableText);
        AccountDeviceConsentStatusValue.Text = BuildDeviceCaptureConsentStatus(currentDevice, deviceCaptureConsent, signedIn);
        AccountDeviceConsentVersionValue.Text = deviceCaptureConsent?.ConsentVersion
            ?? (_latestStatus?.ConsentVersion ?? NotAvailableText);
        AccountCaptureAuthorizationValue.Text = BuildCaptureAuthorizationStatus(currentDevice, deviceCaptureConsent, signedIn);
        AccountLinkStatusValue.Text = _latestIdentityLink != null
            ? ToTitleCase(_latestIdentityLink.LinkStatus)
            : NotAvailableText;
        AccountLinkedAtValue.Text = _latestIdentityLink?.LinkedAtUtc.HasValue == true
            ? FormatHeartbeatLabel(_latestIdentityLink.LinkedAtUtc)
            : "Not linked";
        bool localConsentAvailable = _latestStatus != null;
        bool localConsentActive = _latestStatus?.ConsentActive == true;
        AccountConsentActionButton.Content = localConsentAvailable
            ? (localConsentActive ? "Revoke consent on this PC" : "Grant consent on this PC")
            : "Consent controls unavailable";
        AccountConsentActionButton.IsEnabled = localConsentAvailable;
        AccountConsentActionHelpText.Text = localConsentAvailable
            ? (localConsentActive
                ? "Capture is currently allowed on this PC. Use this button if you want to turn collection off."
                : "If the consent overlay did not appear, use this button to allow capture on this PC.")
            : "Consent controls become available when the local service is reachable on this PC.";

        AccountSignOutButton.IsEnabled = signedIn;
        // --- Manage account profile row ---
        AccountManageProfileRow.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (signedIn && account != null)
        {
            string manageDisplayName = !string.IsNullOrWhiteSpace(account.DisplayName)
                ? account.DisplayName
                : account.PrimaryEmail ?? "Account";
            AccountManageAvatarInitial.Text = manageDisplayName.Length > 0
                ? manageDisplayName[0].ToString().ToUpperInvariant()
                : "A";
            AccountManageNameLabel.Text = manageDisplayName;
            AccountManageEmailLabel.Text = account.PrimaryEmail ?? string.Empty;
        }
        // --- Sign out section ---
        AccountSignOutNotSignedInText.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountSignOutSignedInContent.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (signedIn && account != null)
        {
            string soDisplayName = !string.IsNullOrWhiteSpace(account.DisplayName)
                ? account.DisplayName
                : account.PrimaryEmail ?? "Account";
            AccountSignOutAvatarInitial.Text = soDisplayName.Length > 0
                ? soDisplayName[0].ToString().ToUpperInvariant()
                : "A";
            AccountSignOutNameLabel.Text = soDisplayName;
            AccountSignOutEmailLabel.Text = account.PrimaryEmail ?? string.Empty;
        }
        ShowAccountSection(_activeAccountSection);
    }

    private string BuildUserIdHelpText()
    {
        string prefix = "Most people sign in with their work email. If your workspace gave you a different account ID, use that instead.";
        string? example = _accountAccessHelp?.UserIdRules?.Example;
        return string.IsNullOrWhiteSpace(example)
            ? prefix
            : $"{prefix} Example: {example}.";
    }

    private string BuildPasswordHelpText()
    {
        ControlPlanePasswordRules? rules = _accountAccessHelp?.PasswordRules;
        if (rules == null)
        {
            return "Use the password set for your workspace account.";
        }

        var requirements = new List<string>();
        if (rules.RequiresUpper)
        {
            requirements.Add("an uppercase letter");
        }

        if (rules.RequiresLower)
        {
            requirements.Add("a lowercase letter");
        }

        if (rules.RequiresNumber)
        {
            requirements.Add("a number");
        }

        string suffix = requirements.Count switch
        {
            0 => string.Empty,
            1 => $" and include {requirements[0]}",
            2 => $" and include {requirements[0]} and {requirements[1]}",
            _ => $" and include {string.Join(", ", requirements.Take(requirements.Count - 1))}, and {requirements[^1]}"
        };

        return $"Password must be at least {rules.MinLength} characters{suffix}.";
    }

    private string BuildCreateAccountHelpText(bool backendConfigured)
    {
        if (!backendConfigured)
        {
            return "Sign-in is not configured on this PC yet.";
        }

        if (_accountAccessHelp == null)
        {
            return "Use your existing account, or create one here if your workspace allows self-service sign-up.";
        }

        if (!_accountAccessHelp.SignupEnabled)
        {
            return "This server does not allow self-service sign-up. Use the account your admin gave you.";
        }

        if (_accountAccessHelp.SignupCodeRequired)
        {
            return $"Create an account here if this is your first time. Keep your name, sign-in ID, password, and {_accountAccessHelp.SignupCodeLength}-digit admin code ready.";
        }

        return "Create an account here if this is your first time signing in. Fill in the same details you will use every time you come back to this PC.";
    }

    private string BuildCreateDisplayNameHelpText()
    {
        return "Use the name you want to see in your account and on linked devices.";
    }

    private string BuildCreateUserIdHelpText()
    {
        string prefix = "This becomes how you sign in later. Most people use their work email.";
        string? example = _accountAccessHelp?.UserIdRules?.Example;
        return string.IsNullOrWhiteSpace(example)
            ? prefix
            : $"{prefix} Example: {example}.";
    }

    private string BuildCreatePasswordHelpText()
    {
        string baseText = BuildPasswordHelpText();
        return baseText.StartsWith("Use the password", StringComparison.Ordinal)
            ? "Create the password you want to use when you sign in on this PC."
            : $"Create the password you want to use when you sign in on this PC. {baseText}";
    }

    private string BuildSignupCodeHelpText()
    {
        if (_accountAccessHelp?.SignupCodeRequired != true)
        {
            return "Only fill this if your admin gave you a sign-up code.";
        }

        return $"Enter the {_accountAccessHelp.SignupCodeLength}-digit sign-up code your admin gave you.";
    }

    private string BuildRememberedSignInHelpText(bool signedIn)
    {
        if (signedIn)
        {
            return _rememberedSignIn != null
                ? "Your sign-in is saved only for this Windows account on this PC."
                : "You can save your sign-in on this PC so the form is ready next time.";
        }

        if (_rememberedSignIn == null)
        {
            return "Keep this checked if you want PORTA to remember this email and password on this Windows account only.";
        }

        return $"Saved on this PC for {_rememberedSignIn.UserId}. Click Sign in to reuse it, or forget it below.";
    }

    private void RestoreRememberedSignInInputs(bool force)
    {
        _rememberedSignIn = _rememberedSignInStore.Load();

        if (_rememberedSignIn == null)
        {
            if (force)
            {
                AccountUserIdTextBox.Text = string.Empty;
                AccountPasswordBox.Password = string.Empty;
            }

            return;
        }

        if (force || !AccountUserIdTextBox.IsKeyboardFocusWithin)
        {
            AccountUserIdTextBox.Text = _rememberedSignIn.UserId;
        }

        if (force || !AccountPasswordBox.IsKeyboardFocusWithin)
        {
            AccountPasswordBox.Password = _rememberedSignIn.Password;
        }
    }

    private void UpdateRememberedSignIn(string userId, string password)
    {
        if (AccountRememberSignInCheckBox.IsChecked == true)
        {
            _rememberedSignInStore.Save(userId, password);
        }
        else
        {
            _rememberedSignInStore.Clear();
        }

        _rememberedSignIn = _rememberedSignInStore.Load();
    }

    private void ShowAccountSection(AccountSurfaceSection section)
    {
        _activeAccountSection = section;

        bool showSummary = section == AccountSurfaceSection.Summary;
        bool showManage = section == AccountSurfaceSection.ManageAccount;
        bool showWorkspace = section == AccountSurfaceSection.Workspace;
        bool showSubscription = section == AccountSurfaceSection.Subscription;
        bool showDevices = section == AccountSurfaceSection.Devices;
        bool showPrivacy = section == AccountSurfaceSection.Privacy;
        bool showSignOut = section == AccountSurfaceSection.SignOut;

        // Hide the Access card (sign-in form) when on any section other than Summary,
        // so section content is immediately visible without scrolling past the sign-in form.
        AccountAccessCard.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;

        AccountSummarySectionPanel.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;
        ConfigureAccountPairLayout(AccountPrimarySectionsGrid, AccountManageSectionPanel, AccountWorkspaceSectionPanel, showManage, showWorkspace);
        ConfigureAccountPairLayout(AccountSecondarySectionsGrid, AccountSubscriptionSectionPanel, AccountDevicesSectionPanel, showSubscription, showDevices);
        AccountPrivacySectionPanel.Visibility = showPrivacy ? Visibility.Visible : Visibility.Collapsed;
        AccountSignOutSectionPanel.Visibility = showSignOut ? Visibility.Visible : Visibility.Collapsed;

        // Update vertical nav item appearances
        var navItems = new (Border border, TextBlock label, AccountSurfaceSection targetSection)[]
        {
            (AccountNavSummaryBorder,      AccountNavSummaryText,      AccountSurfaceSection.Summary),
            (AccountNavManageBorder,       AccountNavManageText,       AccountSurfaceSection.ManageAccount),
            (AccountNavWorkspaceBorder,    AccountNavWorkspaceText,    AccountSurfaceSection.Workspace),
            (AccountNavSubscriptionBorder, AccountNavSubscriptionText, AccountSurfaceSection.Subscription),
            (AccountNavDevicesBorder,      AccountNavDevicesText,      AccountSurfaceSection.Devices),
            // Privacy nav item removed — handled in the Privacy page
        };
        var inactiveBg    = new SolidColorBrush(Colors.Transparent);
        var activeBg      = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#17171F"));
        var activeColor   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
        var inactiveColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
        foreach (var (border, label, targetSection) in navItems)
        {
            bool isActive = section == targetSection;
            border.Background = isActive ? activeBg : inactiveBg;
            label.Foreground  = isActive ? activeColor : inactiveColor;
            label.FontWeight  = isActive ? FontWeights.Medium : FontWeights.Normal;
        }
        bool signOutActive = section == AccountSurfaceSection.SignOut;
        AccountNavSignOutBorder.Background = signOutActive ? activeBg : inactiveBg;
    }

    private static void ConfigureAccountPairLayout(
        Grid hostGrid,
        Border first,
        Border second,
        bool showFirst,
        bool showSecond)
    {
        hostGrid.Visibility = showFirst || showSecond ? Visibility.Visible : Visibility.Collapsed;
        first.Visibility = showFirst ? Visibility.Visible : Visibility.Collapsed;
        second.Visibility = showSecond ? Visibility.Visible : Visibility.Collapsed;

        if (showFirst)
        {
            Grid.SetColumn(first, 0);
            Grid.SetColumnSpan(first, showSecond ? 1 : 2);
            first.Margin = showSecond ? new Thickness(0, 0, 8, 0) : new Thickness(0);
        }

        if (showSecond)
        {
            Grid.SetColumn(second, showFirst ? 1 : 0);
            Grid.SetColumnSpan(second, showFirst ? 1 : 2);
            second.Margin = showFirst ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        }
    }

    private ControlPlaneDeviceProfile? ResolveCurrentDevice()
    {
        if (_accountProfile?.Devices == null || _latestIdentityLink == null)
        {
            return null;
        }

        return _accountProfile.Devices
            .FirstOrDefault(device => string.Equals(device.DeviceId, _latestIdentityLink.DeviceId, StringComparison.OrdinalIgnoreCase));
    }

    private ControlPlaneConsentRecord? ResolveAccountLegalConsent()
    {
        if (_accountProfile?.Consents == null)
        {
            return null;
        }

        return _accountProfile.Consents
            .Where(consent => string.Equals(consent.Scope, "account_legal", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(consent => consent.UpdatedAt)
            .FirstOrDefault();
    }

    private ControlPlaneConsentRecord? ResolveCurrentDeviceCaptureConsent()
    {
        if (_accountProfile?.Consents == null || _latestIdentityLink == null)
        {
            return null;
        }

        return _accountProfile.Consents
            .Where(consent => string.Equals(consent.Scope, "device_capture", StringComparison.OrdinalIgnoreCase))
            .Where(consent => string.Equals(consent.DeviceId, _latestIdentityLink.DeviceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(consent => consent.UpdatedAt)
            .FirstOrDefault();
    }

    private string BuildDeviceCaptureConsentStatus(
        ControlPlaneDeviceProfile? currentDevice,
        ControlPlaneConsentRecord? deviceCaptureConsent,
        bool signedIn)
    {
        if (!signedIn)
        {
            return NotAvailableText;
        }

        if (currentDevice == null)
        {
            return BuildCurrentDeviceLinkStatusLabel();
        }

        return _latestStatus != null
            ? BuildLocalDeviceConsentStatus()
            : deviceCaptureConsent != null
                ? ToTitleCase(deviceCaptureConsent.ConsentState)
                : NotAvailableText;
    }

    private string BuildCaptureAuthorizationStatus(
        ControlPlaneDeviceProfile? currentDevice,
        ControlPlaneConsentRecord? deviceCaptureConsent,
        bool signedIn)
    {
        if (!signedIn)
        {
            return "Sign in required";
        }

        if (currentDevice == null)
        {
            return BuildCurrentDeviceLinkStatusMessage();
        }

        if (!string.Equals(currentDevice.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return $"Blocked here ({ToTitleCase(currentDevice.Status)})";
        }

        if (_latestStatus?.ConsentActive == true)
        {
            return "Allowed on this PC";
        }

        if (_latestStatus != null)
        {
            return "Signed in, but this PC still needs capture approval.";
        }

        string? consentState = deviceCaptureConsent?.ConsentState;
        if (string.Equals(consentState, "accepted", StringComparison.OrdinalIgnoreCase))
        {
            return "Allowed on this PC";
        }

        return "Signed in, but this PC still needs capture approval.";
    }

    private string BuildCurrentDeviceLinkStatusLabel()
    {
        if (_latestIdentityLink == null)
        {
            return NotAvailableText;
        }

        if (IsIdentityLinkedToDifferentAccount(_latestIdentityLink))
        {
            return "Linked to another account";
        }

        if (!IsIdentityLinked(_latestIdentityLink))
        {
            return "Device not linked";
        }

        return "Link pending sync";
    }

    private string BuildCurrentDeviceLinkStatusMessage()
    {
        if (_latestIdentityLink == null)
        {
            return "This device link is not available yet.";
        }

        if (IsIdentityLinkedToDifferentAccount(_latestIdentityLink))
        {
            return "This device is linked to another account on this PC.";
        }

        if (!IsIdentityLinked(_latestIdentityLink))
        {
            return "This device is not linked to a workspace yet.";
        }

        return "This device link is waiting to sync into the signed-in workspace.";
    }

    private string BuildLocalDeviceConsentStatus()
    {
        if (_latestStatus == null)
        {
            return NotAvailableText;
        }

        if (_latestStatus.ConsentActive)
        {
            return "Accepted on this PC";
        }

        return _latestStatus.ConsentTimestampUtc.HasValue ? "Revoked on this PC" : "Needed on this PC";
    }

    private void SyncAccountDeviceRows(bool signedIn)
    {
        _accountDeviceRows.Clear();

        if (!signedIn || _accountProfile?.Devices == null)
        {
            return;
        }

        string? currentDeviceId = _latestIdentityLink?.DeviceId;
        foreach (ControlPlaneDeviceProfile device in _accountProfile.Devices
                     .OrderByDescending(device => string.Equals(device.DeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(device => device.LastSeenAt))
        {
            bool isCurrentDevice = string.Equals(device.DeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase);
            _accountDeviceRows.Add(new AccountDeviceRowView(
                DeviceId: device.DeviceId,
                DisplayLabel: isCurrentDevice ? $"{device.DeviceDisplayName} (Current device)" : device.DeviceDisplayName,
                DetailLabel: $"{device.MachineName} / {ToTitleCase(device.Platform)} / {device.AgentVersion}",
                StatusLabel: ToTitleCase(device.Status),
                LastSeenLabel: FormatHeartbeatLabel(device.LastSeenAt),
                BadgeLabel: isCurrentDevice ? "Current device" : "Account device",
                CanUnlink: !string.IsNullOrWhiteSpace(device.CurrentAccountId),
                CanRevoke: !string.Equals(device.Status, "revoked", StringComparison.OrdinalIgnoreCase)));
        }
    }

    private string ResolveWorkspaceDisplayName(AccountWorkspaceOption? selectedWorkspace)
    {
        if (selectedWorkspace != null)
        {
            return selectedWorkspace.OrgName;
        }

        if (_latestIdentityLink != null && IsIdentityLinked(_latestIdentityLink))
        {
            return ResolveWorkspaceName(_latestIdentityLink.OrgId) ?? _latestIdentityLink.OrgId ?? "Linked workspace";
        }

        return "No workspace linked";
    }

    private async void AccountConsentActionButton_Click(object sender, RoutedEventArgs e)
    {
        bool revoke = _latestStatus?.ConsentActive == true;
        bool success = revoke
            ? await _apiClient.RevokeConsentAsync().ConfigureAwait(true)
            : await _apiClient.GrantConsentAsync().ConfigureAwait(true);

        _accountStatusMessage = success
            ? (revoke
                ? "Capture consent was revoked on this PC."
                : "Capture consent was granted on this PC.")
            : (revoke
                ? "Could not revoke capture consent on this PC."
                : "Could not grant capture consent on this PC.");

        await RefreshDiagnosticsAsync(manual: true).ConfigureAwait(true);
        PopulateAccountSurface();
    }

    private string BuildWorkspaceLinkStatus(AccountWorkspaceOption? selectedWorkspace)
    {
        if (_latestIdentityLink == null)
        {
            return "This PC is not ready to link yet.";
        }

        if (IsIdentityLinkedToDifferentAccount(_latestIdentityLink))
        {
            return selectedWorkspace != null
                ? $"Current device is already linked to another account on this PC. Link it to {selectedWorkspace.OrgName} to switch."
                : "Current device is already linked to another account on this PC.";
        }

        if (!IsIdentityLinked(_latestIdentityLink))
        {
            return selectedWorkspace != null
                ? $"Current device is not linked yet. Link it to {selectedWorkspace.OrgName}."
                : "Current device is not linked to a workspace yet.";
        }

        string linkedWorkspaceName = ResolveWorkspaceName(_latestIdentityLink.OrgId) ?? _latestIdentityLink.OrgId ?? "this workspace";
        if (selectedWorkspace == null)
        {
            return $"Current device is linked to {linkedWorkspaceName}.";
        }

        return string.Equals(_latestIdentityLink.OrgId, selectedWorkspace.OrgId, StringComparison.OrdinalIgnoreCase)
            ? $"Current device is linked to {selectedWorkspace.OrgName}."
            : $"Current device is linked to {linkedWorkspaceName}. Link it to {selectedWorkspace.OrgName} to move it.";
    }

    private static bool IsIdentityLinked(LocalApiIdentityLinkResponse link)
    {
        return string.Equals(link.LinkStatus, "linked", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(link.AccountId)
            && !string.IsNullOrWhiteSpace(link.OrgId);
    }

    private bool ShouldClearLocalIdentityLink(ControlPlaneDeviceLinkResult? result)
    {
        if (_latestIdentityLink == null || !IsIdentityLinked(_latestIdentityLink) || result == null)
        {
            return false;
        }

        if (string.Equals(result.Device.Status, "revoked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Device.Status, "unlinked", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(result.LinkState, "linked", StringComparison.OrdinalIgnoreCase)
            && (!string.Equals(result.Device.CurrentAccountId, _latestIdentityLink.AccountId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(result.Device.OrgId, _latestIdentityLink.OrgId, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildRemoteDeviceStatusMessage(ControlPlaneDeviceLinkResult result)
    {
        if (string.Equals(result.Device.Status, "revoked", StringComparison.OrdinalIgnoreCase))
        {
            return "This device was revoked from your account and is no longer active.";
        }

        if (string.Equals(result.Device.Status, "unlinked", StringComparison.OrdinalIgnoreCase))
        {
            return "This device was removed from your account.";
        }

        return "This device link changed elsewhere and needs to be linked again here.";
    }

    private bool IsIdentityLinkedToSignedInAccount(LocalApiIdentityLinkResponse? link)
    {
        return _accountSession != null
            && link != null
            && IsIdentityLinked(link)
            && string.Equals(link.AccountId, _accountSession.AccountId, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsIdentityLinkedToDifferentAccount(LocalApiIdentityLinkResponse? link)
    {
        return _accountSession != null
            && link != null
            && IsIdentityLinked(link)
            && !string.Equals(link.AccountId, _accountSession.AccountId, StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveWorkspaceName(string? orgId)
    {
        if (string.IsNullOrWhiteSpace(orgId))
        {
            return null;
        }

        return _accountWorkspaceOptions
            .FirstOrDefault(option => string.Equals(option.OrgId, orgId, StringComparison.OrdinalIgnoreCase))
            ?.OrgName;
    }

    private string BuildSignedOutStatusMessage()
    {
        if (_rememberedSignIn != null)
        {
            return "Signed out. Your saved sign-in is still ready on this PC.";
        }

        return _latestIdentityLink != null && IsIdentityLinked(_latestIdentityLink)
            ? "Signed out. This PC still remembers its workspace link until you change it."
            : "Signed out. Sign in again to view or change account access on this PC.";
    }

    private string LookupDeviceDisplayName(string deviceId)
    {
        ControlPlaneDeviceProfile? match = _accountProfile?.Devices
            ?.FirstOrDefault(device => string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        return match?.DeviceDisplayName ?? deviceId;
    }

    private async Task<bool> UnlinkManagedDeviceAsync(string deviceId)
    {
        if (_accountSession == null || string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        ControlPlaneDeviceCallResult response = await _controlPlaneAccountClient
            .UnlinkMyDeviceAsync(_accountSession.AccessToken, deviceId)
            .ConfigureAwait(true);
        if (!response.Ok)
        {
            if (response.SessionRejected)
            {
                HandleSessionRejected(DeviceLinkOperation.UnlinkManagedDevice);
                return false;
            }

            _accountStatusMessage = DeviceLinkStatusCopy.BuildFailureMessage(
                DeviceLinkOperation.UnlinkManagedDevice,
                response.Outcome);
            return false;
        }

        if (_latestIdentityLink != null
            && string.Equals(_latestIdentityLink.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            LocalApiIdentityLinkResponse? local = await _identityLinkCoordinator.ClearAsync().ConfigureAwait(true);
            if (local != null)
            {
                _latestIdentityLink = local;
            }
        }

        await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
        _accountStatusMessage = $"{LookupDeviceDisplayName(deviceId)} was unlinked.";
        return true;
    }

    private async Task<bool> RevokeManagedDeviceAsync(string deviceId)
    {
        if (_accountSession == null || string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        ControlPlaneDeviceCallResult response = await _controlPlaneAccountClient
            .RevokeMyDeviceAsync(_accountSession.AccessToken, deviceId)
            .ConfigureAwait(true);
        if (!response.Ok)
        {
            if (response.SessionRejected)
            {
                HandleSessionRejected(DeviceLinkOperation.RevokeManagedDevice);
                return false;
            }

            _accountStatusMessage = DeviceLinkStatusCopy.BuildFailureMessage(
                DeviceLinkOperation.RevokeManagedDevice,
                response.Outcome);
            return false;
        }

        if (_latestIdentityLink != null
            && string.Equals(_latestIdentityLink.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            LocalApiIdentityLinkResponse? local = await _identityLinkCoordinator.ClearAsync().ConfigureAwait(true);
            if (local != null)
            {
                _latestIdentityLink = local;
            }
        }

        await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
        _accountStatusMessage = $"{LookupDeviceDisplayName(deviceId)} was revoked.";
        return true;
    }

    private static string FormatSessionExpiry(DateTimeOffset expiryUtc)
    {
        TimeSpan remaining = expiryUtc - DateTimeOffset.UtcNow;
        string relative = remaining <= TimeSpan.Zero
            ? "expired"
            : remaining.TotalHours >= 1
                ? $"{Math.Max(1, (int)Math.Round(remaining.TotalHours))}h left"
                : $"{Math.Max(1, (int)Math.Round(remaining.TotalMinutes))}m left";
        return $"{expiryUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({relative})";
    }

    private static string ToTitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NotAvailableText;
        }

        string normalized = value.Replace('_', ' ').Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
    }

    private async void AccountNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Account);
        ShowAccountSection(AccountSurfaceSection.Summary); // default to "Signed in as" on every open
        await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
    }

    private async Task<bool> TrySignInAsync(string userId, string password)
    {
        ControlPlaneLoginResult result = await _controlPlaneAccountClient.LoginAsync(userId, password).ConfigureAwait(true);
        if (!result.Ok || result.Response == null)
        {
            _accountStatusMessage = result.Message ?? "Sign-in failed.";
            return false;
        }

        ControlPlaneLoginResponse response = result.Response;
        _accountSession = new ControlPlaneSessionState(
            response.AccessToken,
            response.User.AccountId,
            response.User.UserId,
            response.User.Role,
            response.User.AuthProvider,
            response.SessionExpiresAtUtc,
            response.ReauthExpiresAtUtc,
            DateTimeOffset.UtcNow);
        _accountSessionStore.Save(_accountSession);
        UpdateRememberedSignIn(userId, password);
        AccountPasswordBox.Password = string.Empty;
        _activeAccountSection = AccountSurfaceSection.Summary;

        await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
        return true;
    }

    
    private void AccountNavItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string tag)
        {
            AccountSurfaceSection section = tag switch
            {
                "manage"       => AccountSurfaceSection.ManageAccount,
                "workspace"    => AccountSurfaceSection.Workspace,
                "subscription" => AccountSurfaceSection.Subscription,
                "devices"      => AccountSurfaceSection.Devices,
                "signout"      => AccountSurfaceSection.SignOut,
                _              => AccountSurfaceSection.Summary
            };
            ShowAccountSection(section);
        }
    }
private void AccountSectionMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
        {
            return;
        }

        AccountSurfaceSection section = tag switch
        {
            "manage" => AccountSurfaceSection.ManageAccount,
            "workspace" => AccountSurfaceSection.Workspace,
            "subscription" => AccountSurfaceSection.Subscription,
            "devices" => AccountSurfaceSection.Devices,
            "privacy" => AccountSurfaceSection.Privacy,
            "signout" => AccountSurfaceSection.SignOut,
            _ => AccountSurfaceSection.Summary
        };

        ShowAccountSection(section);
    }

    private void ScrollAccountToTop()
    {
        AccountContentScrollViewer?.ScrollToTop();
    }

    private async void AccountSignInButton_Click(object sender, RoutedEventArgs e)
    {
        string userId = AccountUserIdTextBox.Text?.Trim() ?? string.Empty;
        string password = AccountPasswordBox.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
        {
            _accountStatusMessage = "Enter your email or user ID and password.";
            PopulateAccountSurface();
            ScrollAccountToTop();
            return;
        }

        AccountSignInButton.IsEnabled = false;
        _accountStatusMessage = "Signing in...";
        PopulateAccountSurface();
        ScrollAccountToTop();

        try
        {
            await TrySignInAsync(userId, password).ConfigureAwait(true);
        }
        finally
        {
            AccountSignInButton.IsEnabled = true;
            PopulateAccountSurface();
            ScrollAccountToTop();
        }
    }

    private async void AccountCreateAccountButton_Click(object sender, RoutedEventArgs e)
    {
        string displayName = AccountRegisterDisplayNameTextBox.Text?.Trim() ?? string.Empty;
        string userId = AccountRegisterUserIdTextBox.Text?.Trim() ?? string.Empty;
        string password = AccountRegisterPasswordBox.Password ?? string.Empty;
        string signupCode = AccountRegisterSignupCodeTextBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
        {
            _accountStatusMessage = "Enter your display name, email, and password to create an account.";
            PopulateAccountSurface();
            ScrollAccountToTop();
            return;
        }

        if (_accountAccessHelp?.SignupCodeRequired == true && string.IsNullOrWhiteSpace(signupCode))
        {
            _accountStatusMessage = "Enter the sign-up code your admin gave you.";
            PopulateAccountSurface();
            ScrollAccountToTop();
            return;
        }

        AccountCreateAccountButton.IsEnabled = false;
        _accountStatusMessage = "Creating account...";
        PopulateAccountSurface();
        ScrollAccountToTop();

        try
        {
            ControlPlaneRegisterResult result = await _controlPlaneAccountClient
                .RegisterAsync(userId, password, displayName, signupCode)
                .ConfigureAwait(true);

            if (!result.Ok)
            {
                _accountStatusMessage = result.Message ?? "Could not create the account.";
                return;
            }

            AccountUserIdTextBox.Text = userId;
            AccountPasswordBox.Password = password;
            AccountRegisterPasswordBox.Password = string.Empty;
            AccountRegisterSignupCodeTextBox.Text = string.Empty;
            await TrySignInAsync(userId, password).ConfigureAwait(true);
        }
        finally
        {
            AccountCreateAccountButton.IsEnabled = true;
            PopulateAccountSurface();
            ScrollAccountToTop();
        }
    }

    private async void AccountRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
    }

    private async void AccountSaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accountSession == null || _accountProfile == null)
        {
            return;
        }

        string displayName = AccountDisplayNameTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            _accountStatusMessage = "Enter a display name.";
            PopulateAccountSurface();
            return;
        }

        string? notificationEmail = AccountNotificationEmailTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(notificationEmail) && !notificationEmail.Contains('@'))
        {
            _accountStatusMessage = "Enter a valid notification email address.";
            PopulateAccountSurface();
            return;
        }

        AccountSaveProfileButton.IsEnabled = false;
        try
        {
            ControlPlaneProfileUpdateResponse? response = await _controlPlaneAccountClient
                .UpdateProfileAsync(_accountSession.AccessToken, displayName, string.IsNullOrWhiteSpace(notificationEmail) ? null : notificationEmail)
                .ConfigureAwait(true);
            if (response == null || !response.Ok)
            {
                _accountStatusMessage = "Could not update your account.";
                return;
            }

            await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
            _accountStatusMessage = "Your account was updated.";
        }
        finally
        {
            AccountSaveProfileButton.IsEnabled = true;
            PopulateAccountSurface();
        }
    }

    private void AccountWorkspaceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateAccountSurface();
    }

    private async void AccountLinkWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountWorkspaceComboBox.SelectedItem is not AccountWorkspaceOption workspace)
        {
            return;
        }

        AccountLinkWorkspaceButton.IsEnabled = false;
        try
        {
            bool linked = await LinkCurrentDeviceToWorkspaceAsync(workspace, updateStatusMessage: true).ConfigureAwait(true);
            if (linked)
            {
                await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
                _accountStatusMessage = $"Current device linked to {workspace.OrgName}.";
                PopulateAccountSurface();
            }
            else
            {
                PopulateAccountSurface();
            }
        }
        finally
        {
            AccountLinkWorkspaceButton.IsEnabled = true;
        }
    }

    private async void AccountSignOutButton_Click(object sender, RoutedEventArgs e)
    {
        AccountSignOutButton.IsEnabled = false;
        try
        {
            if (_accountSession != null)
            {
                await _controlPlaneAccountClient.LogoutAsync(_accountSession.AccessToken).ConfigureAwait(true);
            }

            _accountSessionStore.Clear();
            _accountSession = null;
            _accountProfile = null;
            _accountWorkspaceOptions.Clear();
            _accountDeviceRows.Clear();
            _lastAccountRefreshUtc = DateTimeOffset.MinValue;
            _activeAccountSection = AccountSurfaceSection.Summary;

            await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
            RestoreRememberedSignInInputs(force: true);
            _accountStatusMessage = BuildSignedOutStatusMessage();
        }
        finally
        {
            AccountSignOutButton.IsEnabled = true;
            PopulateAccountSurface();
        }
    }

    private async void AccountDeviceUnlinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string deviceId || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        string deviceLabel = LookupDeviceDisplayName(deviceId);
        if (MessageBox.Show(
                $"Unlink {deviceLabel} from this account?",
                "PORTA Control Center",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            bool ok = await UnlinkManagedDeviceAsync(deviceId).ConfigureAwait(true);
            if (!ok)
            {
                PopulateAccountSurface();
            }
        }
        finally
        {
            button.IsEnabled = true;
            PopulateAccountSurface();
        }
    }

    private async void AccountDeviceRevokeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string deviceId || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        string deviceLabel = LookupDeviceDisplayName(deviceId);
        if (MessageBox.Show(
                $"Revoke {deviceLabel}? Revoked devices are no longer treated as active account-linked devices.",
                "PORTA Control Center",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            bool ok = await RevokeManagedDeviceAsync(deviceId).ConfigureAwait(true);
            if (!ok)
            {
                PopulateAccountSurface();
            }
        }
        finally
        {
            button.IsEnabled = true;
            PopulateAccountSurface();
        }
    }

    private void AccountForgetRememberedSignInButton_Click(object sender, RoutedEventArgs e)
    {
        _rememberedSignInStore.Clear();
        _rememberedSignIn = null;
        AccountUserIdTextBox.Text = string.Empty;
        AccountPasswordBox.Password = string.Empty;
        AccountRememberSignInCheckBox.IsChecked = false;
        _accountStatusMessage = "Saved sign-in cleared from this PC.";
        PopulateAccountSurface();
    }
}





