using System.Globalization;
using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter.Updates;

internal sealed record WindowsUpdatePresentationState(
    bool HasUpdate,
    string Title,
    string Summary,
    string Status,
    bool AccountBadgeVisible,
    bool GlobalButtonVisible,
    string GlobalButtonText,
    bool TaskbarOverlayVisible,
    string TaskbarDescription);

internal static class WindowsUpdatePresentationStateBuilder
{
    public static ControlPlaneWindowsReleaseManifest? ResolveAvailableRelease(
        string uiVersion,
        ControlPlaneWindowsReleaseManifest? release)
    {
        if (release == null
            || string.IsNullOrWhiteSpace(release.Version)
            || string.IsNullOrWhiteSpace(release.MsiUrl))
        {
            return null;
        }

        return CompareVersionStrings(release.Version, uiVersion) > 0 ? release : null;
    }

    public static WindowsUpdatePresentationState Build(
        string uiVersion,
        ControlPlaneWindowsReleaseManifest release,
        string? statusText)
    {
        string title = BuildTitle(release);
        string summary = BuildSummary(uiVersion, release);
        string status = string.IsNullOrWhiteSpace(statusText)
            ? BuildStatusText(uiVersion, release)
            : statusText.Trim();
        bool required = release.Required || IsMinimumVersionRequired(uiVersion, release);

        return new WindowsUpdatePresentationState(
            HasUpdate: true,
            Title: title,
            Summary: summary,
            Status: status,
            AccountBadgeVisible: true,
            GlobalButtonVisible: true,
            GlobalButtonText: required ? "Update required" : "Update ready",
            TaskbarOverlayVisible: true,
            TaskbarDescription: $"PORTA | Ari {release.Version} update available");
    }

    public static string BuildTitle(ControlPlaneWindowsReleaseManifest release)
    {
        if (!string.IsNullOrWhiteSpace(release.Title))
        {
            return release.Title.Trim();
        }

        return release.Required
            ? $"Ari {release.Version} is required on this PC"
            : $"Ari {release.Version} is ready";
    }

    public static string BuildSummary(string uiVersion, ControlPlaneWindowsReleaseManifest release)
    {
        string published = release.PublishedAtUtc.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        string requiredNote = IsMinimumVersionRequired(uiVersion, release)
            ? $"This update is required because version {uiVersion} is below the supported floor."
            : release.Required
                ? "This update is marked as required for signed-in users."
                : "Install it now or come back later from the Account screen.";
        string summary = string.IsNullOrWhiteSpace(release.Summary)
            ? "A newer Windows build is available for this machine."
            : release.Summary.Trim();
        return $"{summary} Published {published}. {requiredNote}";
    }

    public static string BuildStatusText(string uiVersion, ControlPlaneWindowsReleaseManifest release)
    {
        return $"This PC is on {uiVersion}. Install {release.Version} now and Ari will update in place, then reopen automatically.";
    }

    public static bool IsMinimumVersionRequired(string uiVersion, ControlPlaneWindowsReleaseManifest release)
    {
        return !string.IsNullOrWhiteSpace(release.MinimumSupportedVersion)
            && CompareVersionStrings(uiVersion, release.MinimumSupportedVersion) < 0;
    }

    public static int CompareVersionStrings(string? left, string? right)
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

    internal static int[] ParseVersionParts(string? version)
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
}
