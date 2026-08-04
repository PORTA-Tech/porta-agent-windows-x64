using System.IO;

namespace Porta.Agent.Common.Identity;

/// <summary>
/// Deletes the v1 "remember my sign-in" file, which stored the user's actual PASSWORD
/// (DPAPI CurrentUser-scoped) so the Control Center could pre-fill the sign-in form.
///
/// A stored password is reusable everywhere that account is valid, cannot be scoped,
/// and cannot be revoked without a password change. v2 stores a refresh token instead:
/// revocable, rotating, and useless for anything but /auth/refresh.
///
/// The file is never read here. Migration is deletion, not conversion -- signing in on
/// the user's behalf to convert a password into a token would mean handling the
/// plaintext password one more time, which is the thing being removed.
///
/// This lives in Common, and both the Control Center and Porta.UserAgent call it,
/// because the Control Center has no autostart entry (Product.wxs gives it shortcuts
/// only). Purging from the Control Center alone would leave the password on disk
/// indefinitely on every machine where nobody happens to open it. Porta.UserAgent
/// autostarts and runs as the user, so it reaches the file on the next sign-in.
/// </summary>
public static class LegacyRememberedSignInPurge
{
    private const string LegacyFileName = "control-center.remembered-signin.v1.json";

    /// <summary>
    /// Deletes the legacy file if it exists. Returns true only when a file was actually
    /// removed, so the caller can log the one-time event without logging every startup.
    /// Never throws: a locked or already-deleted file must not take down startup.
    /// </summary>
    public static bool PurgeStoredPassword()
    {
        try
        {
            string path = BuildLegacyPath();
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildLegacyPath()
    {
        // Fully qualified: this assembly has its own Porta.Agent.Common.Environment
        // namespace, which shadows System.Environment inside it.
        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", LegacyFileName);
    }
}
