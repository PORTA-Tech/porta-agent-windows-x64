using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Porta.Agent.Common.Identity;

namespace Porta.ControlCenter.Identity;

internal sealed record RememberedSignInState(
    string UserId,
    string RefreshToken,
    DateTimeOffset SavedAtUtc,
    DateTimeOffset? ExpiresAtUtc)
{
    /// <summary>
    /// The control plane is the authority on expiry -- this is a local shortcut that
    /// avoids a round trip for a token we already know is dead. A null expiry means the
    /// server did not tell us, so we try and let it answer.
    /// </summary>
    public bool IsExpired => ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= DateTimeOffset.UtcNow;
}

/// <summary>
/// Stores the refresh token for "remember my sign-in", DPAPI CurrentUser-scoped.
///
/// v1 stored the PASSWORD here. The scope was right; the contents were not. A refresh
/// token can be revoked, expires, rotates on every use, and is redeemable at exactly one
/// endpoint -- none of which is true of a password. See LegacyRememberedSignInPurge for
/// what happens to the v1 file.
/// </summary>
internal sealed class ControlPlaneRememberedSignInStore
{
    // Distinct from the v1 entropy on purpose: a v1 blob must not decrypt into a v2
    // record even by accident. v1 is deleted unread, never migrated.
    private static readonly byte[] RefreshTokenEntropy =
        Encoding.UTF8.GetBytes("Porta.ControlCenter.RememberedSignIn.v2");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record PersistedRememberedSignInEnvelope(
        string UserId,
        string ProtectedRefreshToken,
        DateTimeOffset SavedAtUtc,
        DateTimeOffset? ExpiresAtUtc);

    public RememberedSignInState? Load()
    {
        // Every entry point purges v1, so opening the Control Center at all is enough to
        // remove a stored password even if the user never signs in again.
        LegacyRememberedSignInPurge.PurgeStoredPassword();

        try
        {
            string path = BuildPath();
            if (!File.Exists(path))
            {
                return null;
            }

            PersistedRememberedSignInEnvelope? envelope =
                JsonSerializer.Deserialize<PersistedRememberedSignInEnvelope>(File.ReadAllText(path), JsonOptions);
            if (envelope == null
                || string.IsNullOrWhiteSpace(envelope.UserId)
                || string.IsNullOrWhiteSpace(envelope.ProtectedRefreshToken))
            {
                return null;
            }

            var state = new RememberedSignInState(
                envelope.UserId,
                Unprotect(envelope.ProtectedRefreshToken),
                envelope.SavedAtUtc,
                envelope.ExpiresAtUtc);

            if (state.IsExpired)
            {
                Clear();
                return null;
            }

            return state;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string userId, string refreshToken, DateTimeOffset? expiresAtUtc)
    {
        LegacyRememberedSignInPurge.PurgeStoredPassword();

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        string root = Path.GetDirectoryName(BuildPath())!;
        Directory.CreateDirectory(root);

        var envelope = new PersistedRememberedSignInEnvelope(
            userId.Trim(),
            Protect(refreshToken),
            DateTimeOffset.UtcNow,
            expiresAtUtc);

        File.WriteAllText(BuildPath(), JsonSerializer.Serialize(envelope, JsonOptions));
    }

    public void Clear()
    {
        LegacyRememberedSignInPurge.PurgeStoredPassword();

        try
        {
            string path = BuildPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string BuildPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "control-center.remembered-signin.v2.json");
    }

    private static string Protect(string plain)
    {
        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain),
            RefreshTokenEntropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string encoded)
    {
        byte[] raw = Convert.FromBase64String(encoded);
        byte[] plain = ProtectedData.Unprotect(raw, RefreshTokenEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
