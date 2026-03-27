using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter;

public partial class MainWindow
{
    private static readonly TimeSpan ClientReleaseCheckInterval = TimeSpan.FromMinutes(30);
    private static readonly ImageSource UpdateTaskbarOverlayImage = BuildUpdateTaskbarOverlayImage();

    private ControlPlaneWindowsReleaseManifest? _latestWindowsRelease;
    private DateTimeOffset _lastWindowsReleaseCheckUtc = DateTimeOffset.MinValue;
    private string? _dismissedWindowsReleaseVersion;
    private bool _updateOverlayOpen;
    private bool _updateInstallInProgress;
    private string _updateStatusMessage = "App updates are checked automatically when a release is published.";

    private async Task RefreshWindowsReleaseAsync(bool force)
    {
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
                _updateStatusMessage = $"This PC is on version {_uiVersion}.";
            }

            return;
        }

        bool isNewVersion = !string.Equals(previousVersion, _latestWindowsRelease.Version, StringComparison.OrdinalIgnoreCase);
        if (isNewVersion || !_updateInstallInProgress)
        {
            _updateStatusMessage = BuildUpdateStatusText(_latestWindowsRelease);
        }

        if (isNewVersion)
        {
            _dismissedWindowsReleaseVersion = null;
        }

        if (!string.Equals(_dismissedWindowsReleaseVersion, _latestWindowsRelease.Version, StringComparison.OrdinalIgnoreCase))
        {
            _updateOverlayOpen = true;
        }
    }

    private ControlPlaneWindowsReleaseManifest? ResolveAvailableWindowsRelease(ControlPlaneWindowsReleaseManifest? release)
    {
        if (release == null
            || string.IsNullOrWhiteSpace(release.Version)
            || string.IsNullOrWhiteSpace(release.MsiUrl))
        {
            return null;
        }

        return CompareVersionStrings(release.Version, _uiVersion) > 0 ? release : null;
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
        string title = BuildUpdateTitle(release);
        string summary = BuildUpdateSummary(release);
        string status = string.IsNullOrWhiteSpace(_updateStatusMessage)
            ? BuildUpdateStatusText(release)
            : _updateStatusMessage;

        AccountUpdateTitleText.Text = title;
        AccountUpdateBodyText.Text = summary;
        AccountUpdateStatusText.Text = status;
        AccountNavUpdateBadgeText.Text = "1";
        AccountNavUpdateBadgeBorder.Visibility = Visibility.Visible;
        AccountNavButton.ToolTip = $"Ari {release.Version} update available";

        UpdateOverlayTitleText.Text = title;
        UpdateOverlayVersionText.Text = $"Current version {_uiVersion} -> {release.Version}";
        UpdateOverlaySummaryText.Text = summary;
        UpdateOverlayStatusText.Text = status;
        UpdateOverlayInstallButton.IsEnabled = !_updateInstallInProgress;
        UpdateOverlayLaterButton.IsEnabled = !_updateInstallInProgress;
        UpdateOverlay.Visibility = _updateOverlayOpen ? Visibility.Visible : Visibility.Collapsed;

        GlobalUpdateButtonText.Text = release.Required || IsMinimumVersionRequired(release)
            ? "Update required"
            : "Update ready";
        GlobalUpdateButton.ToolTip = summary;
        GlobalUpdateButton.Visibility = Visibility.Visible;
        GlobalUpdateButtonBadgeText.Text = "1";

        if (WindowTaskbarItemInfo != null)
        {
            WindowTaskbarItemInfo.Overlay = UpdateTaskbarOverlayImage;
            WindowTaskbarItemInfo.Description = $"PORTA | Ari {release.Version} update available";
        }
    }

    private static string BuildUpdateTitle(ControlPlaneWindowsReleaseManifest release)
    {
        if (!string.IsNullOrWhiteSpace(release.Title))
        {
            return release.Title.Trim();
        }

        return release.Required
            ? $"Ari {release.Version} is required on this PC"
            : $"Ari {release.Version} is ready";
    }

    private string BuildUpdateSummary(ControlPlaneWindowsReleaseManifest release)
    {
        string published = release.PublishedAtUtc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        string requiredNote = IsMinimumVersionRequired(release)
            ? $"This update is required because version {_uiVersion} is below the supported floor."
            : release.Required
                ? "This update is marked as required for signed-in users."
                : "Install it now, or come back later from the Account screen.";
        string summary = string.IsNullOrWhiteSpace(release.Summary)
            ? "A newer Windows build is available for this machine."
            : release.Summary.Trim();
        return $"{summary} Published {published}. {requiredNote}";
    }

    private string BuildUpdateStatusText(ControlPlaneWindowsReleaseManifest release)
    {
        return $"This PC is on {_uiVersion}. Download {release.Version} when you are ready.";
    }

    private bool IsMinimumVersionRequired(ControlPlaneWindowsReleaseManifest release)
    {
        return !string.IsNullOrWhiteSpace(release.MinimumSupportedVersion)
            && CompareVersionStrings(_uiVersion, release.MinimumSupportedVersion) < 0;
    }

    private static int CompareVersionStrings(string? left, string? right)
    {
        int[] leftParts = ParseVersionParts(left);
        int[] rightParts = ParseVersionParts(right);
        int length = Math.Max(leftParts.Length, rightParts.Length);
        for (int index = 0; index < length; index++)
        {
            int leftValue = index < leftParts.Length ? leftParts[index] : 0;
            int rightValue = index < rightParts.Length ? rightParts[index] : 0;
            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return 0;
    }

    private static int[] ParseVersionParts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return [0];
        }

        string normalized = version.Trim();
        int suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        string[] segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return [0];
        }

        var values = new int[segments.Length];
        for (int index = 0; index < segments.Length; index++)
        {
            values[index] = int.TryParse(segments[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? Math.Max(parsed, 0)
                : 0;
        }

        return values;
    }

    private async Task InstallLatestWindowsReleaseAsync()
    {
        if (_latestWindowsRelease == null || _updateInstallInProgress)
        {
            return;
        }

        try
        {
            _updateInstallInProgress = true;
            _updateStatusMessage = $"Downloading Ari {_latestWindowsRelease.Version}...";
            PopulateAccountSurface();

            string installerPath = await DownloadWindowsReleaseInstallerAsync(_latestWindowsRelease).ConfigureAwait(true);
            _updateStatusMessage = $"Installer downloaded. Windows will ask for permission to install Ari {_latestWindowsRelease.Version}.";
            PopulateAccountSurface();

            LaunchInstaller(installerPath);
            _updateStatusMessage = $"Installer launched for Ari {_latestWindowsRelease.Version}. Finish the Windows installer to update this PC.";
            _updateOverlayOpen = false;
            ControlCenterFileLog.Info($"windows_update_installer_launched version={_latestWindowsRelease.Version}");
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
            PopulateAccountSurface();
        }
    }

    private async Task<string> DownloadWindowsReleaseInstallerAsync(ControlPlaneWindowsReleaseManifest release)
    {
        if (!Uri.TryCreate(release.MsiUrl, UriKind.Absolute, out Uri? downloadUri))
        {
            throw new InvalidOperationException("The release MSI URL is invalid.");
        }

        if (!string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !downloadUri.IsLoopback)
        {
            throw new InvalidOperationException("Update downloads must use HTTPS.");
        }

        string updatesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortaAgent",
            "Updates");
        Directory.CreateDirectory(updatesRoot);

        string safeVersion = SanitizeVersionForFileName(release.Version);
        string targetPath = Path.Combine(updatesRoot, $"AriAgentInstaller-{safeVersion}.msi");
        string tempPath = targetPath + ".download";

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
                File.Delete(tempPath);
                throw new InvalidOperationException("The downloaded update did not match the expected signature.");
            }
        }

        File.Move(tempPath, targetPath, overwrite: true);
        return targetPath;
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

    private static void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The downloaded installer was not found.", installerPath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{installerPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? AppContext.BaseDirectory
        });
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

        ShowSection(UiSection.Account);
        _activeAccountSection = AccountSurfaceSection.Summary;
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
}
