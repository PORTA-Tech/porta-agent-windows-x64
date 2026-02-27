using Microsoft.Win32;
using Porta.Agent.LocalApi;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Porta.Tray;

internal sealed class RegulationActuationEngine : IDisposable
{
    private const string PushNotificationsPath = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string PushToastEnabledValue = "ToastEnabled";
    private const string NotificationSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string QuietHoursToastsEnabledValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    private readonly ScreenBrightnessController _brightnessController = new();
    private readonly DisplayDimmer _displayDimmer = new();
    private bool _focusAssistApplied;
    private bool _notificationReductionApplied;
    private bool _animationsReducedApplied;
    private bool _brightnessDimApplied;
    private byte? _previousBrightnessPercent;
    private int? _previousToastEnabled;
    private int? _previousQuietHoursToastsEnabled;
    private bool? _previousClientAreaAnimationsEnabled;
    private bool _disposed;

    public string LastSummary { get; private set; } = "Actuation: none";

    public void Apply(LocalApiStatusResponse? status, LocalApiSettingsResponse? settings)
    {
        if (_disposed)
        {
            return;
        }

        if (status == null || !status.ConsentActive || status.Paused || settings == null)
        {
            Reset("Actuation: paused/offline/no consent");
            return;
        }

        string action = status.LastAction ?? string.Empty;
        bool overloadNudge = string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal);
        bool focusProtectionNudge = string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal);
        bool fatigueNudge = string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal);
        bool restrict = string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal);
        bool nudgeOrRestrict = overloadNudge || focusProtectionNudge || fatigueNudge || restrict;

        bool shouldEnableFocusAssist = (overloadNudge || focusProtectionNudge || restrict) && settings.FocusModeEnabled;
        bool shouldReduceNotifications = (overloadNudge || restrict) && settings.NotificationReductionEnabled;
        bool shouldReduceAnimations = (overloadNudge || fatigueNudge || restrict) && settings.AnimationReductionEnabled;
        bool shouldApplyTemporaryRestriction = restrict && settings.TemporaryAppRestrictionEnabled;
        bool shouldDimDisplay = shouldApplyTemporaryRestriction || (fatigueNudge && settings.AnimationReductionEnabled);

        ApplyFocusAssist(shouldEnableFocusAssist);
        ApplyNotificationReduction(shouldReduceNotifications);
        ApplyAnimationReduction(shouldReduceAnimations);
        ApplyDisplayDimming(shouldDimDisplay);

        var applied = new List<string>(4);
        if (shouldEnableFocusAssist)
        {
            applied.Add("DND");
        }

        if (shouldReduceNotifications)
        {
            applied.Add("notifications reduced");
        }

        if (shouldReduceAnimations)
        {
            applied.Add("animations reduced");
        }

        if (shouldDimDisplay)
        {
            applied.Add("display dimmed");
        }

        if (shouldApplyTemporaryRestriction)
        {
            applied.Add("temp app restriction active");
        }

        LastSummary = applied.Count == 0
            ? "Actuation: none"
            : $"Actuation: {string.Join(", ", applied)}";
    }

    public void Reset(string reason)
    {
        if (_disposed)
        {
            return;
        }

        ApplyFocusAssist(false);
        ApplyNotificationReduction(false);
        ApplyAnimationReduction(false);
        ApplyDisplayDimming(false);
        LastSummary = reason;
    }

    private void ApplyFocusAssist(bool enable)
    {
        if (enable && !_focusAssistApplied)
        {
            _previousQuietHoursToastsEnabled ??= TryReadCurrentUserDword(NotificationSettingsPath, QuietHoursToastsEnabledValue);
            TryWriteCurrentUserDword(NotificationSettingsPath, QuietHoursToastsEnabledValue, 0);
            _focusAssistApplied = true;
            return;
        }

        if (!enable && _focusAssistApplied)
        {
            RestoreCurrentUserDword(NotificationSettingsPath, QuietHoursToastsEnabledValue, _previousQuietHoursToastsEnabled);
            _focusAssistApplied = false;
        }
    }

    private void ApplyNotificationReduction(bool enable)
    {
        if (enable && !_notificationReductionApplied)
        {
            _previousToastEnabled ??= TryReadCurrentUserDword(PushNotificationsPath, PushToastEnabledValue);
            TryWriteCurrentUserDword(PushNotificationsPath, PushToastEnabledValue, 0);
            _notificationReductionApplied = true;
            return;
        }

        if (!enable && _notificationReductionApplied)
        {
            RestoreCurrentUserDword(PushNotificationsPath, PushToastEnabledValue, _previousToastEnabled);
            _notificationReductionApplied = false;
        }
    }

    private void ApplyAnimationReduction(bool enable)
    {
        if (enable && !_animationsReducedApplied)
        {
            _previousClientAreaAnimationsEnabled ??= TryGetClientAreaAnimationsEnabled();
            TrySetClientAreaAnimationsEnabled(false);
            _animationsReducedApplied = true;
            return;
        }

        if (!enable && _animationsReducedApplied)
        {
            bool previous = _previousClientAreaAnimationsEnabled ?? true;
            TrySetClientAreaAnimationsEnabled(previous);
            _animationsReducedApplied = false;
        }
    }

    private void ApplyDisplayDimming(bool enable)
    {
        if (enable)
        {
            if (!_brightnessDimApplied)
            {
                if (_brightnessController.TryGetCurrentBrightness(out byte currentBrightness))
                {
                    byte target = (byte)Math.Clamp((int)Math.Round(currentBrightness * 0.90d), 0, 100);
                    if (_brightnessController.TrySetBrightness(target))
                    {
                        _previousBrightnessPercent = currentBrightness;
                        _brightnessDimApplied = true;
                        _displayDimmer.SetEnabled(false, 0d);
                        return;
                    }
                }
            }

            // Hardware brightness may be unavailable (external display / policy).
            _displayDimmer.SetEnabled(true, 0.10d);
        }
        else
        {
            if (_brightnessDimApplied && _previousBrightnessPercent.HasValue)
            {
                _brightnessController.TrySetBrightness(_previousBrightnessPercent.Value);
            }

            _brightnessDimApplied = false;
            _previousBrightnessPercent = null;
            _displayDimmer.SetEnabled(false, 0d);
        }
    }

    private static int? TryReadCurrentUserDword(string subKeyPath, string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            object? raw = key?.GetValue(valueName);
            return raw is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteCurrentUserDword(string subKeyPath, string valueName, int value)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
            key?.SetValue(valueName, value, RegistryValueKind.DWord);
        }
        catch
        {
            // Best-effort only. Diagnostic surfaces still show intervention intent.
        }
    }

    private static void RestoreCurrentUserDword(string subKeyPath, string valueName, int? previousValue)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
            if (key == null)
            {
                return;
            }

            if (previousValue.HasValue)
            {
                key.SetValue(valueName, previousValue.Value, RegistryValueKind.DWord);
                return;
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static bool? TryGetClientAreaAnimationsEnabled()
    {
        try
        {
            bool enabled = false;
            bool ok = SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0);
            return ok ? enabled : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetClientAreaAnimationsEnabled(bool enabled)
    {
        try
        {
            bool value = enabled;
            _ = SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, ref value, SPIF_SENDCHANGE);
        }
        catch
        {
            // Best-effort only.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reset("Actuation: disposed");
        _displayDimmer.Dispose();
    }

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    private const uint SPIF_SENDCHANGE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);
}

internal sealed class ScreenBrightnessController
{
    public bool TryGetCurrentBrightness(out byte brightnessPercent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementObject obj in searcher.Get())
            {
                object? raw = obj["CurrentBrightness"];
                if (raw is byte b)
                {
                    brightnessPercent = b;
                    return true;
                }
            }
        }
        catch
        {
            // Best-effort.
        }

        brightnessPercent = 0;
        return false;
    }

    public bool TrySetBrightness(byte brightnessPercent)
    {
        try
        {
            using var mclass = new ManagementClass(@"root\WMI", "WmiMonitorBrightnessMethods", null);
            foreach (ManagementObject instance in mclass.GetInstances())
            {
                object? active = instance["Active"];
                if (active is bool isActive && !isActive)
                {
                    continue;
                }

                instance.InvokeMethod("WmiSetBrightness", [1u, brightnessPercent]);
                return true;
            }
        }
        catch
        {
            // Best-effort.
        }

        return false;
    }
}

internal sealed class DisplayDimmer : IDisposable
{
    private readonly List<DimOverlayForm> _overlays = [];
    private bool _enabled;
    private bool _disposed;

    public void SetEnabled(bool enabled, double opacity)
    {
        if (_disposed)
        {
            return;
        }

        if (!enabled)
        {
            HideAll();
            _enabled = false;
            return;
        }

        if (!_enabled)
        {
            ShowAll(opacity);
            _enabled = true;
            return;
        }

        foreach (DimOverlayForm overlay in _overlays.ToArray())
        {
            if (!TrySetOverlayOpacity(overlay, opacity))
            {
                _overlays.Remove(overlay);
            }
        }
    }

    private void ShowAll(double opacity)
    {
        if (_overlays.Count > 0)
        {
            foreach (DimOverlayForm overlay in _overlays.ToArray())
            {
                if (!TrySetOverlayOpacity(overlay, opacity))
                {
                    _overlays.Remove(overlay);
                    continue;
                }

                if (!overlay.Visible)
                {
                    TryShowOverlay(overlay);
                }
            }

            if (_overlays.Count == 0)
            {
                // Handles can become invalid on display topology changes.
                CreateOverlays(opacity);
            }

            return;
        }

        CreateOverlays(opacity);
    }

    private void CreateOverlays(double opacity)
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            try
            {
                var overlay = new DimOverlayForm(screen.Bounds, opacity);
                _overlays.Add(overlay);
                overlay.Show();
            }
            catch
            {
                // Best-effort only. Never crash tray on dimming failures.
            }
        }
    }

    private void HideAll()
    {
        foreach (DimOverlayForm overlay in _overlays.ToArray())
        {
            if (overlay.IsDisposed)
            {
                _overlays.Remove(overlay);
                continue;
            }

            if (overlay.Visible)
            {
                try
                {
                    overlay.Hide();
                }
                catch
                {
                    _overlays.Remove(overlay);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (DimOverlayForm overlay in _overlays.ToArray())
        {
            try
            {
                if (!overlay.IsDisposed)
                {
                    overlay.Close();
                    overlay.Dispose();
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        _overlays.Clear();
    }

    private static bool TrySetOverlayOpacity(DimOverlayForm overlay, double opacity)
    {
        if (overlay.IsDisposed)
        {
            return false;
        }

        try
        {
            overlay.Opacity = Math.Clamp(opacity, 0.02d, 0.35d);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryShowOverlay(DimOverlayForm overlay)
    {
        try
        {
            overlay.Show();
        }
        catch
        {
            // Best-effort only.
        }
    }
}

internal sealed class DimOverlayForm : Form
{
    public DimOverlayForm(Rectangle bounds, double opacity)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = bounds;
        BackColor = Color.Black;
        Opacity = Math.Clamp(opacity, 0.02d, 0.35d);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExTransparent = 0x00000020;
            const int WsExLayered = 0x00080000;
            const int WsExToolWindow = 0x00000080;

            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow;
            return cp;
        }
    }
}
