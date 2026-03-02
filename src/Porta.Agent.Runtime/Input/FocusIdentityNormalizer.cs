using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Porta.Agent.Runtime.Input;

internal static class FocusIdentityNormalizer
{
    private const string RootDirectoryName = "PORTA";
    private const string SecretsDirectoryName = "secrets";
    private const string InstallSecretFileName = "install_secret.bin";
    private const string InstallSecretEntropyContext = "PORTA/install_secret/v1";
    private static readonly byte[] InstallSecretEntropy = Encoding.UTF8.GetBytes(InstallSecretEntropyContext);
    private static readonly Lazy<byte[]> InstallSecret = new(LoadInstallSecret, isThreadSafe: true);
    private static readonly ConcurrentDictionary<uint, CacheItem> Cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    internal static string ComputeAppHash(uint processId)
    {
        if (processId == 0)
        {
            return "unknown";
        }

        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (Cache.TryGetValue(processId, out CacheItem cached) && nowTicks <= cached.ExpiresAtUtcTicks)
        {
            return cached.AppHash;
        }

        string stableIdentifier = ResolveStableAppIdentifier(processId);
        string appHash = HashIdentifier(stableIdentifier);
        Cache[processId] = new CacheItem(appHash, DateTimeOffset.UtcNow.Add(CacheTtl).UtcTicks);
        return appHash;
    }

    private static string ResolveStableAppIdentifier(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById(checked((int)processId));

            string? path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return $"exe:{path.Trim().ToLowerInvariant()}";
            }

            if (!string.IsNullOrWhiteSpace(process.ProcessName))
            {
                return $"process:{process.ProcessName.Trim().ToLowerInvariant()}";
            }
        }
        catch
        {
        }

        return $"pid:{processId}";
    }

    private static string HashIdentifier(string stableIdentifier)
    {
        byte[] secret = InstallSecret.Value;
        byte[] payload = Encoding.UTF8.GetBytes(stableIdentifier);
        using var hmac = new HMACSHA256(secret);
        byte[] hash = hmac.ComputeHash(payload);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static byte[] LoadInstallSecret()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string path = Path.Combine(root, RootDirectoryName, SecretsDirectoryName, InstallSecretFileName);
            if (!File.Exists(path))
            {
                return BuildFallbackKey();
            }

            byte[] protectedBytes = File.ReadAllBytes(path);
            byte[] secret = ProtectedData.Unprotect(
                protectedBytes,
                InstallSecretEntropy,
                DataProtectionScope.LocalMachine);

            return secret.Length == 32 ? secret : BuildFallbackKey();
        }
        catch
        {
            return BuildFallbackKey();
        }
    }

    private static byte[] BuildFallbackKey()
    {
        // Fallback keeps hashing deterministic locally if install_secret is unavailable.
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.MachineName}|PORTA|focus_identity_fallback"));
    }

    private readonly record struct CacheItem(string AppHash, long ExpiresAtUtcTicks);
}
