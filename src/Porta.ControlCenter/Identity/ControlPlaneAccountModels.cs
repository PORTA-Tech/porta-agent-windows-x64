using System.Text.Json.Serialization;

namespace Porta.ControlCenter.Identity;

internal sealed record ControlPlaneLoginRequest(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("password")] string Password);

internal sealed record ControlPlaneRegisterRequest(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("signup_code")] string? SignupCode);

internal sealed record ControlPlaneLoginUser(
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("auth_provider")] string? AuthProvider);

internal sealed record ControlPlaneLoginResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("user")] ControlPlaneLoginUser User,
    [property: JsonPropertyName("session_expires_at_utc")] DateTimeOffset SessionExpiresAtUtc,
    [property: JsonPropertyName("reauth_expires_at_utc")] DateTimeOffset ReauthExpiresAtUtc);

internal sealed record ControlPlaneRegisterResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("user")] ControlPlaneLoginUser User);

internal sealed record ControlPlaneUserIdRules(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("min_length")] int MinLength,
    [property: JsonPropertyName("max_length")] int MaxLength,
    [property: JsonPropertyName("example")] string? Example);

internal sealed record ControlPlanePasswordRules(
    [property: JsonPropertyName("min_length")] int MinLength,
    [property: JsonPropertyName("requires_upper")] bool RequiresUpper,
    [property: JsonPropertyName("requires_lower")] bool RequiresLower,
    [property: JsonPropertyName("requires_number")] bool RequiresNumber);

internal sealed record ControlPlaneAuthAccessHelpResponse(
    [property: JsonPropertyName("signup_enabled")] bool SignupEnabled,
    [property: JsonPropertyName("admin_signup_enabled")] bool AdminSignupEnabled,
    [property: JsonPropertyName("signup_code_required")] bool SignupCodeRequired,
    [property: JsonPropertyName("signup_code_length")] int SignupCodeLength,
    [property: JsonPropertyName("user_id_rules")] ControlPlaneUserIdRules UserIdRules,
    [property: JsonPropertyName("password_rules")] ControlPlanePasswordRules PasswordRules);

internal sealed record ControlPlaneLogoutResponse(
    [property: JsonPropertyName("ok")] bool Ok);

internal sealed record ControlPlaneApiErrorResponse(
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record ControlPlaneLoginResult(
    bool Ok,
    ControlPlaneLoginResponse? Response,
    string? ErrorCode,
    string? Message);

internal sealed record ControlPlaneRegisterResult(
    bool Ok,
    ControlPlaneRegisterResponse? Response,
    string? ErrorCode,
    string? Message);

internal sealed record ControlPlaneProfileUpdateRequest(
    [property: JsonPropertyName("display_name")] string DisplayName);

internal sealed record ControlPlaneProfileUpdateResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("account")] ControlPlaneAccountProfile Account);

internal sealed record ControlPlaneAccountProfile(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("primaryEmail")] string PrimaryEmail,
    [property: JsonPropertyName("emailVerified")] bool EmailVerified,
    [property: JsonPropertyName("avatarUrl")] string? AvatarUrl,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("authProvider")] string? AuthProvider,
    [property: JsonPropertyName("lastLoginAt")] DateTimeOffset? LastLoginAt,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

internal sealed record ControlPlaneOrganizationProfile(
    [property: JsonPropertyName("orgId")] string OrgId,
    [property: JsonPropertyName("orgName")] string OrgName,
    [property: JsonPropertyName("orgSlug")] string? OrgSlug,
    [property: JsonPropertyName("planTier")] string PlanTier,
    [property: JsonPropertyName("billingAccountId")] string? BillingAccountId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

internal sealed record ControlPlaneMembership(
    [property: JsonPropertyName("membershipId")] string MembershipId,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("orgId")] string OrgId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("invitedAt")] DateTimeOffset? InvitedAt,
    [property: JsonPropertyName("joinedAt")] DateTimeOffset? JoinedAt,
    [property: JsonPropertyName("invitedByAccountId")] string? InvitedByAccountId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

internal sealed record ControlPlaneAccountMembershipView(
    [property: JsonPropertyName("membership")] ControlPlaneMembership Membership,
    [property: JsonPropertyName("organization")] ControlPlaneOrganizationProfile? Organization);

internal sealed record ControlPlaneDeviceProfile(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("deviceDisplayName")] string DeviceDisplayName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("currentAccountId")] string? CurrentAccountId,
    [property: JsonPropertyName("revokedAt")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

internal sealed record ControlPlaneConsentRecord(
    [property: JsonPropertyName("consentId")] string ConsentId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("deviceId")] string? DeviceId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("consentVersion")] string ConsentVersion,
    [property: JsonPropertyName("consentState")] string ConsentState,
    [property: JsonPropertyName("telemetryLevel")] string? TelemetryLevel,
    [property: JsonPropertyName("consentedAt")] DateTimeOffset ConsentedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

internal sealed record ControlPlaneMeProfileView(
    [property: JsonPropertyName("account")] ControlPlaneAccountProfile Account,
    [property: JsonPropertyName("organizations")] IReadOnlyList<ControlPlaneAccountMembershipView> Organizations,
    [property: JsonPropertyName("devices")] IReadOnlyList<ControlPlaneDeviceProfile> Devices,
    [property: JsonPropertyName("consents")] IReadOnlyList<ControlPlaneConsentRecord> Consents);

internal sealed record ControlPlaneDeviceRegistrationRequest(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("deviceDisplayName")] string DeviceDisplayName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("sessionToken")] string? SessionToken,
    [property: JsonPropertyName("localProofToken")] string? LocalProofToken);

internal sealed record ControlPlaneDeviceAccountLinkRequest(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("orgId")] string OrgId,
    [property: JsonPropertyName("sessionToken")] string? SessionToken,
    [property: JsonPropertyName("localProofToken")] string? LocalProofToken);

internal sealed record ControlPlaneDeviceAccountUnlinkRequest(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("sessionToken")] string? SessionToken,
    [property: JsonPropertyName("localProofToken")] string? LocalProofToken);

internal sealed record ControlPlaneDeviceLinkResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("linkState")] string LinkState,
    [property: JsonPropertyName("device")] ControlPlaneDeviceProfile Device);

internal sealed record ControlPlaneSessionState(
    string AccessToken,
    string AccountId,
    string UserId,
    string Role,
    string? AuthProvider,
    DateTimeOffset SessionExpiresAtUtc,
    DateTimeOffset ReauthExpiresAtUtc,
    DateTimeOffset SavedAtUtc);

internal sealed record AccountWorkspaceOption(
    string OrgId,
    string OrgName,
    string Role,
    string PlanTier)
{
    public string Label => $"{OrgName} - {ToTitleCase(Role)}";

    private static string ToTitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}

internal sealed record AccountDeviceRowView(
    string DeviceId,
    string DisplayLabel,
    string DetailLabel,
    string StatusLabel,
    string LastSeenLabel,
    string BadgeLabel,
    bool CanUnlink,
    bool CanRevoke);
