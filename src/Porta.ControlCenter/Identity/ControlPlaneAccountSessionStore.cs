using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Porta.ControlCenter.Identity;

internal sealed class ControlPlaneAccountSessionStore
{
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("Porta.ControlCenter.ControlPlaneSession.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record PersistedSessionEnvelope(
        string ProtectedAccessToken,
        string AccountId,
        string UserId,
        string Role,
        string? AuthProvider,
        DateTimeOffset SessionExpiresAtUtc,
        DateTimeOffset ReauthExpiresAtUtc,
        DateTimeOffset SavedAtUtc);

    public ControlPlaneSessionState? Load()
    {
        try
        {
            string path = BuildPath();
            if (!File.Exists(path))
            {
                return null;
            }

            PersistedSessionEnvelope? envelope = JsonSerializer.Deserialize<PersistedSessionEnvelope>(File.ReadAllText(path), JsonOptions);
            if (envelope == null)
            {
                return null;
            }

            if (envelope.SessionExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                Clear();
                return null;
            }

            string token = Unprotect(envelope.ProtectedAccessToken);
            return new ControlPlaneSessionState(
                token,
                envelope.AccountId,
                envelope.UserId,
                envelope.Role,
                envelope.AuthProvider,
                envelope.SessionExpiresAtUtc,
                envelope.ReauthExpiresAtUtc,
                envelope.SavedAtUtc);
        }
        catch
        {
            return null;
        }
    }

    public void Save(ControlPlaneSessionState state)
    {
        string root = Path.GetDirectoryName(BuildPath())!;
        Directory.CreateDirectory(root);

        var envelope = new PersistedSessionEnvelope(
            Protect(state.AccessToken),
            state.AccountId,
            state.UserId,
            state.Role,
            state.AuthProvider,
            state.SessionExpiresAtUtc,
            state.ReauthExpiresAtUtc,
            DateTimeOffset.UtcNow);

        File.WriteAllText(BuildPath(), JsonSerializer.Serialize(envelope, JsonOptions));
    }

    public void Clear()
    {
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
        return Path.Combine(root, "PortaAgent", "control-center.account-session.v1.json");
    }

    private static string Protect(string plain)
    {
        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain),
            TokenEntropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string encoded)
    {
        byte[] raw = Convert.FromBase64String(encoded);
        byte[] plain = ProtectedData.Unprotect(raw, TokenEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
