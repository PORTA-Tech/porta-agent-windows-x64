using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Porta.ControlCenter.Identity;
using Porta.Agent.Common.Updates;
using Porta.ControlCenter.Updates;

namespace Porta.ControlCenter;

public partial class MainWindow
{
    // Silent auto-update era: the service installs updates on its own, so the
    // Control Center never interrupts. The Updates page still works manually;
    // only the unsolicited overlay / badges / taskbar nag are disabled.
    private const bool AutoUpdatePromptingEnabled = false;

    private const string UpdateServiceName = "AriAgent";
    private const string UpdateInstallHelperFileName = "windows-update.install.ps1";
    private const string UpdateInstallStatusFileName = "windows-update.status.json";
    private static readonly TimeSpan ClientReleaseCheckInterval = TimeSpan.FromMinutes(30);
    private static readonly ImageSource UpdateTaskbarOverlayImage = BuildUpdateTaskbarOverlayImage();

    private ControlPlaneWindowsReleaseManifest? _latestWindowsRelease;
    private DateTimeOffset _lastWindowsReleaseCheckUtc = DateTimeOffset.MinValue;
    private string? _dismissedWindowsReleaseVersion;
    private bool _updateOverlayOpen;
    private bool _updateInstallInProgress;
    private string _updateStatusMessage = "App updates are checked automatically when a release is published.";
    private bool _updateOutcomeLoaded;
    private UpdateInstallOutcome? _latestUpdateOutcome;

    private async Task RefreshWindowsReleaseAsync(bool force)
    {
        EnsureUpdateOutcomeLoaded();

        bool shouldRefresh = force
            || _latestWindowsRelease == null
            || (DateTimeOffset.UtcNow - _lastWindowsReleaseCheckUtc) >= ClientReleaseCheckInterval;
        if (!shouldRefresh)
        {
            return;
        }

        _lastWindowsReleaseCheckUtc = DateTimeOffset.UtcNow;
        ControlPlaneWindowsReleaseManifest? release = await _controlPlaneAccountClient.GetLatestWindowsReleaseAsync().ConfigureAwait(true);
        string? previousVersion = _latestWindowsRelease?.Version;
        _latestWindowsRelease = ResolveAvailableWindowsRelease(release);

        if (_latestWindowsRelease == null)
        {
            _updateOverlayOpen = false;
            if (!_updateInstallInProgress)
            {
                _updateStatusMessage = _latestUpdateOutcome != null
                    ? BuildUpdateOutcomeStatusText(_latestUpdateOutcome)
                    : $"This PC is on version {_uiVersion}.";
            }

            return;
        }

        bool isNewVersion = !string.Equals(previousVersion, _latestWindowsRelease.Version, StringComparison.OrdinalIgnoreCase);
        bool showOutcomeForCurrentRelease = _latestUpdateOutcome != null
            && !string.Equals(_latestUpdateOutcome.Version, _uiVersion, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_latestUpdateOutcome.Version, _latestWindowsRelease.Version, StringComparison.OrdinalIgnoreCase);

        if (!_updateInstallInProgress)
        {
            _updateStatusMessage = showOutcomeForCurrentRelease
                ? BuildUpdateOutcomeStatusText(_latestUpdateOutcome!)
                : BuildUpdateStatusText(_latestWindowsRelease);
        }

        if (isNewVersion)
        {
            _dismissedWindowsReleaseVersion = null;
        }

        if (AutoUpdatePromptingEnabled
            && !string.Equals(_dismissedWindowsReleaseVersion, _latestWindowsRelease.Version, StringComparison.OrdinalIgnoreCase))
        {
            _updateOverlayOpen = true;
        }
    }

    private ControlPlaneWindowsReleaseManifest? ResolveAvailableWindowsRelease(ControlPlaneWindowsReleaseManifest? release)
    {
        return WindowsUpdatePresentationStateBuilder.ResolveAvailableRelease(_uiVersion, release);
    }

    private void PopulateUpdateSurface()
    {
        bool hasUpdate = _latestWindowsRelease != null;

        AccountUpdateBanner.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
        AccountInstallUpdateButton.IsEnabled = hasUpdate && !_updateInstallInProgress;
        AccountDismissUpdateButton.IsEnabled = hasUpdate && !_updateInstallInProgress;

        if (!hasUpdate)
        {
            AccountNavUpdateBadgeBorder.Visibility = Visibility.Collapsed;
            AccountNavButton.ToolTip = "Account, workspace, devices, and updates";
            GlobalUpdateButton.Visibility = Visibility.Collapsed;
            if (WindowTaskbarItemInfo != null)
            {
                WindowTaskbarItemInfo.Overlay = null;
                WindowTaskbarItemInfo.Description = "PORTA";
            }

            UpdateOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        ControlPlaneWindowsReleaseManifest release = _latestWindowsRelease!;
        WindowsUpdatePresentationState presentation = WindowsUpdatePresentationStateBuilder.Build(
            _uiVersion,
            release,
            _updateStatusMessage);

        AccountUpdateTitleText.Text = presentation.Title;
        AccountUpdateBodyText.Text = presentation.Summary;
        AccountUpdateStatusText.Text = presentation.Status;
        AccountNavUpdateBadgeText.Text = "1";
        AccountNavUpdateBadgeBorder.Visibility = AutoUpdatePromptingEnabled && presentation.AccountBadgeVisible ? Visibility.Visible : Visibility.Collapsed;
        AccountNavButton.ToolTip = $"Ari {release.Version} update available";

        UpdateOverlayTitleText.Text = presentation.Title;
        UpdateOverlayVersionText.Text = $"Current version {_uiVersion} -> {release.Version}";
        UpdateOverlaySummaryText.Text = presentation.Summary;
        UpdateOverlayStatusText.Text = presentation.Status;
        UpdateOverlayInstallButton.IsEnabled = !_updateInstallInProgress;
        UpdateOverlayLaterButton.IsEnabled = !_updateInstallInProgress;
        UpdateOverlay.Visibility = _updateOverlayOpen ? Visibility.Visible : Visibility.Collapsed;

        GlobalUpdateButtonText.Text = presentation.GlobalButtonText;
        GlobalUpdateButton.ToolTip = presentation.Summary;
        GlobalUpdateButton.Visibility = AutoUpdatePromptingEnabled && presentation.GlobalButtonVisible ? Visibility.Visible : Visibility.Collapsed;
        GlobalUpdateButtonBadgeText.Text = "1";

        if (WindowTaskbarItemInfo != null)
        {
            WindowTaskbarItemInfo.Overlay = AutoUpdatePromptingEnabled && presentation.TaskbarOverlayVisible ? UpdateTaskbarOverlayImage : null;
            WindowTaskbarItemInfo.Description = AutoUpdatePromptingEnabled ? presentation.TaskbarDescription : "PORTA";
        }
    }

    private static string BuildUpdateTitle(ControlPlaneWindowsReleaseManifest release)
    {
        return WindowsUpdatePresentationStateBuilder.BuildTitle(release);
    }

    private string BuildUpdateSummary(ControlPlaneWindowsReleaseManifest release)
    {
        return WindowsUpdatePresentationStateBuilder.BuildSummary(_uiVersion, release);
    }

    private string BuildUpdateStatusText(ControlPlaneWindowsReleaseManifest release)
    {
        return WindowsUpdatePresentationStateBuilder.BuildStatusText(_uiVersion, release);
    }

    private string BuildUpdateOutcomeStatusText(UpdateInstallOutcome outcome)
    {
        if (outcome.Succeeded)
        {
            return outcome.RestartRequired
                ? $"Ari {outcome.Version} installed successfully. Windows may need a restart to finish the upgrade."
                : $"Ari {outcome.Version} installed successfully.";
        }

        string message = string.IsNullOrWhiteSpace(outcome.Message)
            ? "The update could not finish."
            : outcome.Message.Trim();
        return $"Ari {outcome.Version} could not finish updating. {message}";
    }

    private bool IsMinimumVersionRequired(ControlPlaneWindowsReleaseManifest release)
    {
        return WindowsUpdatePresentationStateBuilder.IsMinimumVersionRequired(_uiVersion, release);
    }

    private static int CompareVersionStrings(string? left, string? right)
    {
        return WindowsUpdatePresentationStateBuilder.CompareVersionStrings(left, right);
    }

    private static int[] ParseVersionParts(string? version)
    {
        return WindowsUpdatePresentationStateBuilder.ParseVersionParts(version);
    }

    private async Task InstallLatestWindowsReleaseAsync()
    {
        if (_latestWindowsRelease == null || _updateInstallInProgress)
        {
            return;
        }

        bool shuttingDownForUpdate = false;

        try
        {
            _updateInstallInProgress = true;
            _updateStatusMessage = $"Downloading Ari {_latestWindowsRelease.Version}...";
            PopulateAccountSurface();

            Uri downloadUri = GetValidatedDownloadUri(_latestWindowsRelease);
            string installerPath = await DownloadWindowsReleaseInstallerAsync(_latestWindowsRelease, downloadUri).ConfigureAwait(true);
            UpdateInstallLaunchPlan launchPlan = PrepareUpdateInstallLaunchPlan(_latestWindowsRelease, installerPath, downloadUri);

            _updateStatusMessage = $"Ari {_latestWindowsRelease.Version} is ready to update. PORTA will close, replace the installed version, and reopen automatically.";
            PopulateAccountSurface();

            LaunchInstaller(launchPlan);
            _updateOverlayOpen = false;
            _dismissedWindowsReleaseVersion = null;
            shuttingDownForUpdate = true;
            ControlCenterFileLog.Info($"windows_update_installer_launched version={_latestWindowsRelease.Version} helper={launchPlan.HelperScriptPath}");

            await Task.Delay(200).ConfigureAwait(true);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _updateStatusMessage = $"Could not install the update: {ex.Message}";
            ControlCenterFileLog.Error("windows_update_install_failed", ex);
            MessageBox.Show(
                this,
                _updateStatusMessage,
                "PORTA Update",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _updateInstallInProgress = false;
            if (!shuttingDownForUpdate && !(Application.Current?.Dispatcher.HasShutdownStarted ?? false))
            {
                PopulateAccountSurface();
            }
        }
    }

    private static Uri GetValidatedDownloadUri(ControlPlaneWindowsReleaseManifest release)
    {
        if (!Uri.TryCreate(release.MsiUrl, UriKind.Absolute, out Uri? downloadUri))
        {
            throw new InvalidOperationException("The release MSI URL is invalid.");
        }

        if (!string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !downloadUri.IsLoopback)
        {
            throw new InvalidOperationException("Update downloads must use HTTPS.");
        }

        return downloadUri;
    }

    private async Task<string> DownloadWindowsReleaseInstallerAsync(ControlPlaneWindowsReleaseManifest release, Uri downloadUri)
    {
        string updatesRoot = GetUpdatesRoot();
        string safeVersion = SanitizeVersionForFileName(release.Version);
        string targetPath = Path.Combine(updatesRoot, $"AriAgentInstaller-{safeVersion}.msi");
        string tempPath = targetPath + ".download";

        if (!downloadUri.IsLoopback && string.IsNullOrWhiteSpace(release.Sha256))
        {
            throw new InvalidOperationException("Published Windows updates must include a SHA-256 checksum.");
        }

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using HttpResponseMessage response = await httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (FileStream destination = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(destination).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                string actual = await ComputeSha256Async(tempPath).ConfigureAwait(false);
                if (!string.Equals(actual, release.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The downloaded update did not match the published SHA-256 checksum.");
                }
            }

            File.Move(tempPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }
    }

    private UpdateInstallLaunchPlan PrepareUpdateInstallLaunchPlan(
        ControlPlaneWindowsReleaseManifest release,
        string installerPath,
        Uri downloadUri)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The downloaded installer was not found.", installerPath);
        }

        string updatesRoot = GetUpdatesRoot();
        string safeVersion = SanitizeVersionForFileName(release.Version);
        string helperScriptPath = Path.Combine(updatesRoot, UpdateInstallHelperFileName);
        string contextPath = Path.Combine(updatesRoot, $"windows-update-{safeVersion}.context.json");
        string logPath = Path.Combine(updatesRoot, $"windows-update-{safeVersion}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.log");
        string statusPath = GetUpdateInstallStatusPath();
        string currentControlCenterPath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Path.Combine(AppContext.BaseDirectory, "Porta.ControlCenter.exe");

        DeleteFileQuietly(statusPath);
        File.WriteAllText(helperScriptPath, BuildUpdateInstallerHelperScript());

        var context = new UpdateInstallContext(
            InstallerPath: installerPath,
            ExpectedSha256: NormalizeHash(release.Sha256),
            RequireSha256Validation: !downloadUri.IsLoopback,
            // Manifest-controlled: unsigned preview builds update via SHA-256 until the
            // signing certificate arrives, then the control plane flips this to true.
            RequireSignatureValidation: release.RequireSignature,
            Version: release.Version,
            ServiceName: UpdateServiceName,
            CurrentControlCenterPid: Environment.ProcessId,
            CurrentControlCenterPath: currentControlCenterPath,
            RestartControlCenterPath: currentControlCenterPath,
            StatusPath: statusPath,
            LogPath: logPath);

        string contextJson = JsonSerializer.Serialize(context);
        File.WriteAllText(contextPath, contextJson);

        return new UpdateInstallLaunchPlan(helperScriptPath, contextPath, context);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizeVersionForFileName(string version)
    {
        string sanitized = string.IsNullOrWhiteSpace(version) ? "latest" : version.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }

        return sanitized;
    }

    private static string NormalizeHash(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    private static string GetUpdatesRoot()
    {
        string updatesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortaAgent",
            "Updates");
        Directory.CreateDirectory(updatesRoot);
        return updatesRoot;
    }

    private static string GetUpdateInstallStatusPath() => Path.Combine(GetUpdatesRoot(), UpdateInstallStatusFileName);

    private void EnsureUpdateOutcomeLoaded()
    {
        if (_updateOutcomeLoaded)
        {
            return;
        }

        _updateOutcomeLoaded = true;
        string statusPath = GetUpdateInstallStatusPath();
        if (!File.Exists(statusPath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(statusPath);
            _latestUpdateOutcome = JsonSerializer.Deserialize<UpdateInstallOutcome>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (_latestUpdateOutcome != null)
            {
                _updateStatusMessage = BuildUpdateOutcomeStatusText(_latestUpdateOutcome);
            }
        }
        catch (Exception ex)
        {
            ControlCenterFileLog.Error("windows_update_outcome_load_failed", ex);
        }
        finally
        {
            DeleteFileQuietly(statusPath);
        }
    }

    private static void DeleteFileQuietly(string path)
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

    private static void LaunchInstaller(UpdateInstallLaunchPlan launchPlan)
    {
        if (!File.Exists(launchPlan.Context.InstallerPath))
        {
            throw new FileNotFoundException("The downloaded installer was not found.", launchPlan.Context.InstallerPath);
        }

        if (!File.Exists(launchPlan.HelperScriptPath))
        {
            throw new FileNotFoundException("The update helper script was not found.", launchPlan.HelperScriptPath);
        }

        string powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powershellPath))
        {
            powershellPath = "powershell.exe";
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{launchPlan.HelperScriptPath}\" -ContextPath \"{launchPlan.ContextPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(launchPlan.Context.InstallerPath) ?? AppContext.BaseDirectory
        });
    }

    private static string BuildUpdateInstallerHelperScript()
    {
        return WindowsUpdateInstallScriptBuilder.Build();
    }

    private void GlobalUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        OpenUpdateSurface();
    }

    private void OpenUpdateSurface()
    {
        if (_latestWindowsRelease == null)
        {
            return;
        }

        // ShowSection puts the Account page on Summary and refreshes it; this only adds the
        // overlay on top. PopulateAccountSurface reads _updateOverlayOpen, so the refresh
        // landing later still renders with the overlay open.
        ShowSection(UiSection.Account);
        _updateOverlayOpen = true;
        PopulateAccountSurface();
        Activate();
    }

    private void AccountDismissUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestWindowsRelease != null)
        {
            _dismissedWindowsReleaseVersion = _latestWindowsRelease.Version;
            _updateStatusMessage = $"Ari {_latestWindowsRelease.Version} is ready when you want to install it.";
        }

        _updateOverlayOpen = false;
        PopulateAccountSurface();
    }

    private async void AccountInstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await InstallLatestWindowsReleaseAsync().ConfigureAwait(true);
    }

    private static ImageSource BuildUpdateTaskbarOverlayImage()
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var outerBrush = new SolidColorBrush(Color.FromRgb(193, 18, 31));
            outerBrush.Freeze();
            var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(248, 113, 113)), 1.5);
            borderPen.Freeze();

            dc.DrawEllipse(outerBrush, borderPen, new Point(size / 2d, size / 2d), 14d, 14d);

            double pixelsPerDip = 1.0d;
            var formattedText = new FormattedText(
                "1",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                16d,
                Brushes.White,
                pixelsPerDip);

            Point textOrigin = new(
                (size - formattedText.Width) / 2d,
                (size - formattedText.Height) / 2d - 0.5d);
            dc.DrawText(formattedText, textOrigin);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed record UpdateInstallContext(
        string InstallerPath,
        string ExpectedSha256,
        bool RequireSha256Validation,
        bool RequireSignatureValidation,
        string Version,
        string ServiceName,
        int CurrentControlCenterPid,
        string CurrentControlCenterPath,
        string RestartControlCenterPath,
        string StatusPath,
        string LogPath);

    private sealed record UpdateInstallLaunchPlan(
        string HelperScriptPath,
        string ContextPath,
        UpdateInstallContext Context);

    private sealed record UpdateInstallOutcome(
        bool Succeeded,
        string Version,
        bool RestartRequired,
        int ExitCode,
        string? Message,
        string? LogPath,
        DateTimeOffset? CompletedAtUtc);
}
