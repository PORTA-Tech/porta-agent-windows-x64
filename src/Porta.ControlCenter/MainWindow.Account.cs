using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Porta.Agent.LocalApi;
using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter;

public partial class MainWindow
{
    private static readonly TimeSpan AccountRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly ControlPlaneAccountClient _controlPlaneAccountClient = new();
    private readonly ControlPlaneAccountSessionStore _accountSessionStore = new();
    private readonly ObservableCollection<AccountWorkspaceOption> _accountWorkspaceOptions = [];
    private LocalIdentityLinkCoordinator _identityLinkCoordinator = null!;
    private ControlPlaneSessionState? _accountSession;
    private ControlPlaneMeProfileView? _accountProfile;
    private LocalApiIdentityLinkResponse? _latestIdentityLink;
    private DateTimeOffset _lastAccountRefreshUtc = DateTimeOffset.MinValue;
    private string _accountStatusMessage = "Sign in to see your PORTA identity and linked device.";

    private void InitializeAccountSurface()
    {
        _identityLinkCoordinator = new LocalIdentityLinkCoordinator(_apiClient);
        _accountSession = _accountSessionStore.Load();

        if (AccountWorkspaceComboBox != null)
        {
            AccountWorkspaceComboBox.ItemsSource = _accountWorkspaceOptions;
            AccountWorkspaceComboBox.DisplayMemberPath = nameof(AccountWorkspaceOption.Label);
        }

        PopulateAccountSurface();
    }

    private async Task RefreshAccountSurfaceAsync(bool force = false)
    {
        _latestIdentityLink = await _identityLinkCoordinator.GetCurrentAsync().ConfigureAwait(true);

        bool shouldRefreshRemote = force
            || _activeSection == UiSection.Account
            || (_accountSession != null && (DateTimeOffset.UtcNow - _lastAccountRefreshUtc) >= AccountRefreshInterval);

        if (_accountSession != null && shouldRefreshRemote)
        {
            await RegisterCurrentDeviceAsync().ConfigureAwait(true);

            ControlPlaneMeProfileView? me = await _controlPlaneAccountClient.GetMeAsync(_accountSession.AccessToken).ConfigureAwait(true);
            if (me == null)
            {
                _accountSessionStore.Clear();
                _accountSession = null;
                _accountProfile = null;
                _accountWorkspaceOptions.Clear();
                _lastAccountRefreshUtc = DateTimeOffset.MinValue;
                _accountStatusMessage = "Account session expired. Sign in again to manage your profile.";
            }
            else
            {
                _accountProfile = me;
                SyncWorkspaceOptions();
                _lastAccountRefreshUtc = DateTimeOffset.UtcNow;
                _accountStatusMessage = $"Signed in to {TrimBackendUrl(_controlPlaneAccountClient.GetConfiguredBaseUrl())}.";
            }
        }

        PopulateAccountSurface();
    }

    private async Task RegisterCurrentDeviceAsync()
    {
        if (_accountSession == null || _latestIdentityLink == null)
        {
            return;
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
            SessionToken: _accountSession.AccessToken,
            LocalProofToken: null);

        await _controlPlaneAccountClient.RegisterDeviceAsync(request, _accountSession.AccessToken).ConfigureAwait(true);
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
            if (membershipView.Organization == null)
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

        ControlPlaneDeviceLinkResult? remote = await _controlPlaneAccountClient.LinkDeviceAsync(
            new ControlPlaneDeviceAccountLinkRequest(
                DeviceId: _latestIdentityLink.DeviceId,
                AccountId: _accountSession.AccountId,
                OrgId: workspace.OrgId,
                SessionToken: _accountSession.AccessToken,
                LocalProofToken: null),
            _accountSession.AccessToken).ConfigureAwait(true);

        if (remote == null || !remote.Ok)
        {
            if (updateStatusMessage)
            {
                _accountStatusMessage = "Could not link this device to the selected workspace.";
                PopulateAccountSurface();
            }

            return false;
        }

        LocalApiIdentityLinkResponse? local = await _identityLinkCoordinator
            .LinkSignedInAccountAsync(_accountSession.AccountId, workspace.OrgId)
            .ConfigureAwait(true);

        if (local == null)
        {
            await _controlPlaneAccountClient.UnlinkDeviceAsync(
                new ControlPlaneDeviceAccountUnlinkRequest(
                    DeviceId: _latestIdentityLink.DeviceId,
                    AccountId: _accountSession.AccountId,
                    OrgId: workspace.OrgId,
                    SessionToken: _accountSession.AccessToken,
                    LocalProofToken: null),
                _accountSession.AccessToken).ConfigureAwait(true);

            if (updateStatusMessage)
            {
                _accountStatusMessage = "Could not update the local device link. No workspace change was saved.";
                PopulateAccountSurface();
            }

            return false;
        }

        _latestIdentityLink = local;

        if (updateStatusMessage)
        {
            _accountStatusMessage = $"Current device linked to {workspace.OrgName}.";
        }

        return true;
    }

    private async Task<bool> UnlinkCurrentDeviceAsync(bool updateStatusMessage)
    {
        if (_latestIdentityLink == null)
        {
            return false;
        }

        if (_accountSession != null && IsIdentityLinked(_latestIdentityLink))
        {
            await _controlPlaneAccountClient.UnlinkDeviceAsync(
                new ControlPlaneDeviceAccountUnlinkRequest(
                    DeviceId: _latestIdentityLink.DeviceId,
                    AccountId: _latestIdentityLink.AccountId,
                    OrgId: _latestIdentityLink.OrgId,
                    SessionToken: _accountSession.AccessToken,
                    LocalProofToken: null),
                _accountSession.AccessToken).ConfigureAwait(true);
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

    private void PopulateAccountSurface()
    {
        string backendUrl = _controlPlaneAccountClient.GetConfiguredBaseUrl() ?? "Not configured";
        AccountBackendValue.Text = backendUrl;
        AccountStatusText.Text = _accountStatusMessage;

        bool signedIn = _accountSession != null && _accountProfile != null;
        AccountSignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountSignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        AccountSignInButton.IsEnabled = !signedIn && !string.IsNullOrWhiteSpace(backendUrl) && backendUrl != "Not configured";
        AccountSignOutButton.IsEnabled = signedIn;
        AccountRefreshButton.IsEnabled = signedIn;

        ControlPlaneAccountProfile? account = _accountProfile?.Account;
        AccountSignedInAsValue.Text = account?.DisplayName ?? "Not signed in";
        AccountEmailValue.Text = account?.PrimaryEmail ?? NotAvailableText;
        AccountAuthProviderValue.Text = account?.AuthProvider ?? _accountSession?.AuthProvider ?? NotAvailableText;
        AccountSessionExpiryValue.Text = _accountSession != null
            ? FormatSessionExpiry(_accountSession.SessionExpiresAtUtc)
            : NotAvailableText;

        if (!AccountDisplayNameTextBox.IsKeyboardFocusWithin)
        {
            AccountDisplayNameTextBox.Text = account?.DisplayName ?? string.Empty;
        }
        AccountReadOnlyEmailValue.Text = account?.PrimaryEmail ?? NotAvailableText;
        AccountProfileStatusText.Text = signedIn
            ? "Profile data comes from the control plane account session."
            : "Sign in to manage your account profile.";
        AccountSaveProfileButton.IsEnabled = signedIn;

        AccountWorkspaceOption? selectedWorkspace = AccountWorkspaceComboBox.SelectedItem as AccountWorkspaceOption
            ?? ResolvePreferredWorkspace();
        AccountWorkspaceComboBox.IsEnabled = signedIn && _accountWorkspaceOptions.Count > 0;
        AccountLinkWorkspaceButton.IsEnabled = signedIn
            && _latestIdentityLink != null
            && selectedWorkspace != null
            && NeedsWorkspaceLink(selectedWorkspace);

        AccountWorkspaceNameValue.Text = ResolveWorkspaceDisplayName(selectedWorkspace);
        AccountWorkspaceRoleValue.Text = selectedWorkspace != null ? ToTitleCase(selectedWorkspace.Role) : NotAvailableText;
        AccountWorkspaceLinkValue.Text = BuildWorkspaceLinkStatus(selectedWorkspace);

        AccountPlanTierValue.Text = selectedWorkspace != null ? ToTitleCase(selectedWorkspace.PlanTier) : NotAvailableText;
        AccountSubscriptionStatusValue.Text = selectedWorkspace != null
            ? $"Plan from {selectedWorkspace.OrgName}"
            : (_latestIdentityLink != null && IsIdentityLinked(_latestIdentityLink))
                ? "Subscription details require an active account session."
                : "Subscription details appear once a workspace is linked.";

        ControlPlaneDeviceProfile? currentDevice = ResolveCurrentDevice();
        AccountCurrentDeviceNameValue.Text = currentDevice?.DeviceDisplayName ?? Environment.MachineName;
        AccountCurrentDeviceIdValue.Text = _latestIdentityLink?.DeviceId ?? NotAvailableText;
        AccountCurrentDeviceStatusValue.Text = currentDevice != null
            ? ToTitleCase(currentDevice.Status)
            : (_latestIdentityLink != null ? ToTitleCase(_latestIdentityLink.LinkStatus) : NotAvailableText);
        AccountCurrentDeviceLastSeenValue.Text = currentDevice != null
            ? FormatHeartbeatLabel(currentDevice.LastSeenAt)
            : (_latestIdentityLink != null ? FormatHeartbeatLabel(_latestIdentityLink.UpdatedAtUtc) : NotAvailableText);

        AccountConsentStatusValue.Text = _latestStatus == null
            ? NotAvailableText
            : (_latestStatus.ConsentActive ? "Accepted" : "Needed");
        AccountConsentVersionValue.Text = _latestStatus?.ConsentVersion ?? NotAvailableText;
        AccountLinkStatusValue.Text = _latestIdentityLink != null
            ? ToTitleCase(_latestIdentityLink.LinkStatus)
            : NotAvailableText;
        AccountLinkedAtValue.Text = _latestIdentityLink?.LinkedAtUtc.HasValue == true
            ? FormatHeartbeatLabel(_latestIdentityLink.LinkedAtUtc)
            : "Not linked";
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

    private string BuildWorkspaceLinkStatus(AccountWorkspaceOption? selectedWorkspace)
    {
        if (_latestIdentityLink == null)
        {
            return "Local device identity is unavailable.";
        }

        if (IsIdentityLinkedToDifferentAccount(_latestIdentityLink))
        {
            return selectedWorkspace != null
                ? $"Current device is linked to another account locally. Link it to {selectedWorkspace.OrgName} to switch."
                : "Current device is linked to another account locally.";
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
        if (_latestIdentityLink == null)
        {
            return "Signed out. Local device identity is unavailable.";
        }

        return IsIdentityLinked(_latestIdentityLink)
            ? "Signed out. This device remains locally linked to its workspace."
            : "Signed out. Sign in to see your PORTA identity and linked device.";
    }

    private static string TrimBackendUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "the control plane";
        }

        return value.TrimEnd('/');
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
        await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
    }

    private async void AccountSignInButton_Click(object sender, RoutedEventArgs e)
    {
        string userId = AccountUserIdTextBox.Text?.Trim() ?? string.Empty;
        string password = AccountPasswordBox.Password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
        {
            _accountStatusMessage = "Enter your user ID and password.";
            PopulateAccountSurface();
            return;
        }

        AccountSignInButton.IsEnabled = false;
        _accountStatusMessage = "Signing in...";
        PopulateAccountSurface();

        try
        {
            ControlPlaneLoginResponse? response = await _controlPlaneAccountClient.LoginAsync(userId, password).ConfigureAwait(true);
            if (response == null)
            {
                _accountStatusMessage = "Sign-in failed. Check your credentials or control-plane connection.";
                return;
            }

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
            AccountPasswordBox.Password = string.Empty;

            await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
        }
        finally
        {
            AccountSignInButton.IsEnabled = true;
            PopulateAccountSurface();
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
            _accountStatusMessage = "Display name cannot be empty.";
            PopulateAccountSurface();
            return;
        }

        AccountSaveProfileButton.IsEnabled = false;
        try
        {
            ControlPlaneProfileUpdateResponse? response = await _controlPlaneAccountClient
                .UpdateProfileAsync(_accountSession.AccessToken, displayName)
                .ConfigureAwait(true);
            if (response == null || !response.Ok)
            {
                _accountStatusMessage = "Could not update your account profile.";
                return;
            }

            await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
            _accountStatusMessage = "Account profile updated.";
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
            _lastAccountRefreshUtc = DateTimeOffset.MinValue;
            AccountPasswordBox.Password = string.Empty;

            await RefreshAccountSurfaceAsync(force: true).ConfigureAwait(true);
            _accountStatusMessage = BuildSignedOutStatusMessage();
        }
        finally
        {
            AccountSignOutButton.IsEnabled = true;
            PopulateAccountSurface();
        }
    }
}
