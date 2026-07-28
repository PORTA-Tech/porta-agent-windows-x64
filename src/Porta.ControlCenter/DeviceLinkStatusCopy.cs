using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter;

internal enum DeviceLinkOperation
{
    LinkCurrentDevice,
    UnlinkCurrentDevice,
    UnlinkManagedDevice,
    RevokeManagedDevice
}

/// <summary>
/// Status-bar copy for device link operations.
///
/// The point of this class is that the three failure outcomes read differently. Before the
/// client distinguished them, every failure said "could not link", so an expired sign-in --
/// the one failure the user can actually fix -- was indistinguishable from being offline.
/// Sessions last 30 days, so every user reaches this eventually.
/// </summary>
internal static class DeviceLinkStatusCopy
{
    internal static bool RequiresReSignIn(ControlPlaneCallOutcome outcome)
    {
        return outcome == ControlPlaneCallOutcome.Unauthorized;
    }

    internal static string BuildFailureMessage(DeviceLinkOperation operation, ControlPlaneCallOutcome outcome)
    {
        return outcome switch
        {
            ControlPlaneCallOutcome.Unauthorized => BuildSessionExpiredMessage(operation),
            ControlPlaneCallOutcome.Unreachable => BuildUnreachableMessage(operation),
            _ => BuildRejectedMessage(operation)
        };
    }

    private static string BuildSessionExpiredMessage(DeviceLinkOperation operation)
    {
        string action = operation switch
        {
            DeviceLinkOperation.LinkCurrentDevice => "link this device",
            DeviceLinkOperation.UnlinkCurrentDevice => "unlink this device",
            DeviceLinkOperation.UnlinkManagedDevice => "unlink that device",
            _ => "revoke that device"
        };

        return $"Your sign-in expired. Sign in again to {action}.";
    }

    // Deliberately says what is still true of the device, because nothing changed remotely.
    private static string BuildUnreachableMessage(DeviceLinkOperation operation)
    {
        string consequence = operation switch
        {
            DeviceLinkOperation.LinkCurrentDevice => "this device was not linked",
            DeviceLinkOperation.UnlinkCurrentDevice => "this device is still linked",
            DeviceLinkOperation.UnlinkManagedDevice => "that device is still linked",
            _ => "that device was not revoked"
        };

        return $"Could not reach the account server, so {consequence}. Check your connection and try again.";
    }

    private static string BuildRejectedMessage(DeviceLinkOperation operation)
    {
        return operation switch
        {
            DeviceLinkOperation.LinkCurrentDevice => "Could not link this device to the selected workspace.",
            DeviceLinkOperation.UnlinkCurrentDevice => "Could not unlink this device. It is still linked to your account.",
            DeviceLinkOperation.UnlinkManagedDevice => "Could not unlink the selected device.",
            _ => "Could not revoke the selected device."
        };
    }
}
