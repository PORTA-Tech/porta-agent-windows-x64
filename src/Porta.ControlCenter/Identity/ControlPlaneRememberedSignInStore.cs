using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Porta.ControlCenter.Identity;

internal sealed record RememberedSignInState(
    string UserId,
    string Password,
    DateTimeOffset SavedAtUtc);

internal sealed class ControlPlaneRememberedSignInStore
{
    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("Porta.ControlCenter.RememberedSignIn.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record PersistedRememberedSignInEnvelope(
        string UserId,
        string ProtectedPassword,
        DateTimeOffset SavedAtUtc);

    public RememberedSignInState? Load()
    {
        try
        {
            string path = BuildPath();
            if (!File.Exists(path))
            {
                return null;
            }

            PersistedRememberedSignInEnvelope? envelope =
                JsonSerializer.Deserialize<PersistedRememberedSignInEnvelope>(File.ReadAllText(path), JsonOptions);
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.UserId) || string.IsNullOrWhiteSpace(envelope.ProtectedPassword))
            {
                return null;
            }

            return new RememberedSignInState(
                envelope.UserId,
                Unprotect(envelope.ProtectedPassword),
                envelope.SavedAtUtc);
        }
        catch
        {
            return null;
        }
    }

    public void Save(string userId, string password)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        string root = Path.GetDirectoryName(BuildPath())!;
        Directory.CreateDirectory(root);

        var envelope = new PersistedRememberedSignInEnvelope(
            userId.Trim(),
            Protect(password),
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
        return Path.Combine(root, "PortaAgent", "control-center.remembered-signin.v1.json");
    }

    private static string Protect(string plain)
    {
        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain),
            PasswordEntropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string encoded)
    {
        byte[] raw = Convert.FromBase64String(encoded);
        byte[] plain = ProtectedData.Unprotect(raw, PasswordEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
