using System;
using Microsoft.Win32;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Service;

internal sealed class WindowsNotificationController : INotificationController
{
    private const string NotificationsSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string ToastsEnabledValueName = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    public NotificationControllerSnapshotResult GetSnapshot()
    {
        if (!TryCheckCapability(out string capabilityErrorCode, out string capabilityErrorDetail))
        {
            return new NotificationControllerSnapshotResult(
                Succeeded: false,
                ErrorCode: capabilityErrorCode,
                ErrorDetail: capabilityErrorDetail,
                Snapshot: BuildInvalidSnapshot());
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(NotificationsSettingsPath, writable: false);
            int? toastsValue = TryReadToastsEnabledValue(key);
            bool toastsEnabled = !toastsValue.HasValue || toastsValue.Value != 0;
            string focusAssistMode = "unknown";
            bool quietHoursEnabled = false;
            var snapshot = new NotificationControllerSnapshot(
                SchemaVer: 1,
                ContractVersion: "v1",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                IsValid: true,
                Provider: "windows_registry",
                FocusAssistMode: focusAssistMode,
                QuietHoursEnabled: quietHoursEnabled,
                ToastBannersEnabled: toastsEnabled,
                RawStateJson: $$"""{"toasts_enabled":{{(toastsEnabled ? "true" : "false")}}}""");
            return new NotificationControllerSnapshotResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                Snapshot: snapshot);
        }
        catch (Exception ex)
        {
            return new NotificationControllerSnapshotResult(
                Succeeded: false,
                ErrorCode: "SNAPSHOT_READ_FAILED",
                ErrorDetail: ex.Message,
                Snapshot: BuildInvalidSnapshot());
        }
    }

    public NotificationControllerActionResult ApplySuppression(NotificationControllerSnapshot snapshot)
    {
        if (!snapshot.IsValid)
        {
            return new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: "SNAPSHOT_INVALID",
                ErrorDetail: "Cannot apply suppression because provided snapshot is invalid.",
                StateChanged: false);
        }

        if (!TryCheckCapability(out string capabilityErrorCode, out string capabilityErrorDetail))
        {
            return new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: capabilityErrorCode,
                ErrorDetail: capabilityErrorDetail,
                StateChanged: false);
        }

        try
        {
            using RegistryKey key = EnsureWritableSettingsKey();
            int? previousValue = TryReadToastsEnabledValue(key);
            bool previousEnabled = !previousValue.HasValue || previousValue.Value != 0;
            if (previousEnabled)
            {
                key.SetValue(ToastsEnabledValueName, 0, RegistryValueKind.DWord);
            }

            return new NotificationControllerActionResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                StateChanged: previousEnabled);
        }
        catch (Exception ex)
        {
            return new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: "SUPPRESSION_APPLY_FAILED",
                ErrorDetail: ex.Message,
                StateChanged: false);
        }
    }

    public NotificationControllerActionResult Restore(NotificationControllerSnapshot snapshot)
    {
        if (!snapshot.IsValid)
        {
            return new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: "SNAPSHOT_INVALID",
                ErrorDetail: "Cannot restore because provided snapshot is invalid.",
                StateChanged: false);
        }

        if (!TryCheckCapability(out string capabilityErrorCode, out string capabilityErrorDetail))
        {
            return new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: capabilityErrorCode,
                ErrorDetail: capabilityErrorDetail,
                StateChanged: false);
        }

        try
        {
            using RegistryKey key = EnsureWritableSettingsKey();
            int? currentValue = TryReadToastsEnabledValue(key);
            bool currentEnabled = !currentValue.HasValue || currentValue.Value != 0;
            bool targetEnabled = snapshot.ToastBannersEnabled;
            if (currentEnabled != targetEnabled)
            {
                key.SetValue(ToastsEnabledValueName, targetEnabled ? 1 : 0, RegistryValueKind.DWord);
            }

            return new NotificationControllerActionResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                StateChanged: currentEnabled != targetEnabled);
        }
        catch (Exception ex)
        {
            return new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: "SUPPRESSION_RESTORE_FAILED",
                ErrorDetail: ex.Message,
                StateChanged: false);
        }
    }

    private static bool TryCheckCapability(out string errorCode, out string errorDetail)
    {
        errorCode = string.Empty;
        errorDetail = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            errorCode = "UNSUPPORTED_OS";
            errorDetail = "Notification suppression adapter supports Windows only.";
            return false;
        }

        if (!Environment.UserInteractive)
        {
            errorCode = "NON_INTERACTIVE_SESSION";
            errorDetail = "Current session is non-interactive; user notification state is not safely writable.";
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(NotificationsSettingsPath, writable: true);
            if (key == null)
            {
                errorCode = "NOTIFICATION_SETTINGS_UNAVAILABLE";
                errorDetail = "Notification settings registry key is unavailable for current user.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            errorCode = "NOTIFICATION_CAPABILITY_CHECK_FAILED";
            errorDetail = ex.Message;
            return false;
        }
    }

    private static RegistryKey EnsureWritableSettingsKey()
    {
        RegistryKey? key = Registry.CurrentUser.CreateSubKey(NotificationsSettingsPath, writable: true);
        return key ?? throw new InvalidOperationException("Unable to open writable notification settings key.");
    }

    private static int? TryReadToastsEnabledValue(RegistryKey? key)
    {
        if (key == null)
        {
            return null;
        }

        object? raw = key.GetValue(ToastsEnabledValueName, null, RegistryValueOptions.None);
        if (raw == null)
        {
            return null;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out int parsed) => parsed,
            _ => null
        };
    }

    private static NotificationControllerSnapshot BuildInvalidSnapshot()
    {
        return new NotificationControllerSnapshot(
            SchemaVer: 1,
            ContractVersion: "v1",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            IsValid: false,
            Provider: "windows_registry",
            FocusAssistMode: "unknown",
            QuietHoursEnabled: false,
            ToastBannersEnabled: true,
            RawStateJson: "{}");
    }
}
