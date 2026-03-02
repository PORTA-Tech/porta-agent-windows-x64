using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Porta.Agent.LocalApi;

public sealed record LocalApiRuntimeSettings(
    int Port,
    string AuthToken,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record LocalApiEndpointSettings(
    string Host,
    int Port,
    DateTimeOffset UpdatedAtUtc);

public static class LocalApiRuntimeSettingsStore
{
    private const int DefaultPort = 17887;
    private const int MaxPort = 17950;
    private const string SettingsFileName = "local-api.secure.json";
    private const string LegacySettingsFileName = "local-api.json";
    private const string EndpointFileName = "local-api.endpoint.json";
    private const string TokenProtectionMode = "dpapi_localmachine";
    private static readonly byte[] TokenEntropy = [0x50, 0x4F, 0x52, 0x54, 0x41, 0x5F, 0x4C, 0x4F, 0x43, 0x41, 0x4C, 0x5F, 0x41, 0x50, 0x49];

    public static LocalApiRuntimeSettings LoadOrCreate(int? preferredPort = null)
    {
        string path = GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        LocalApiRuntimeSettings? existing = TryLoad(path);
        if (existing == null)
        {
            existing = TryLoad(GetLegacySettingsPath());
        }

        if (preferredPort is null && existing != null && IsPortAvailable(existing.Port))
        {
            LocalApiRuntimeSettings stable = existing with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            Save(path, stable);
            SaveEndpoint(stable.Port);
            return stable;
        }

        int port = FindAvailablePort(preferredPort ?? existing?.Port ?? DefaultPort);
        string token = existing?.AuthToken ?? GenerateToken();
        var settings = new LocalApiRuntimeSettings(
            Port: port,
            AuthToken: token,
            CreatedAtUtc: existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        Save(path, settings);
        SaveEndpoint(settings.Port);
        return settings;
    }

    public static LocalApiRuntimeSettings? Load()
    {
        return TryLoad(GetSettingsPath());
    }

    public static LocalApiEndpointSettings? LoadEndpoint()
    {
        try
        {
            string path = GetEndpointPath();
            if (!File.Exists(path))
            {
                return null;
            }

            string raw = File.ReadAllText(path);
            EndpointDocument? doc = JsonSerializer.Deserialize<EndpointDocument>(raw);
            if (doc == null || doc.Port < 1 || doc.Port > 65535)
            {
                return null;
            }

            string host = ParseLoopbackHost(doc.Host);
            return new LocalApiEndpointSettings(host, doc.Port, doc.UpdatedAtUtc);
        }
        catch
        {
            return null;
        }
    }

    private static LocalApiRuntimeSettings? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string raw = File.ReadAllText(path);
            SettingsDocument? doc = JsonSerializer.Deserialize<SettingsDocument>(raw);
            if (doc == null)
            {
                return null;
            }

            string? token = TryUnprotectToken(doc.AuthTokenProtected);
            if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(doc.AuthToken))
            {
                token = doc.AuthToken;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            return new LocalApiRuntimeSettings(
                Port: doc.Port,
                AuthToken: token,
                CreatedAtUtc: doc.CreatedAtUtc,
                UpdatedAtUtc: doc.UpdatedAtUtc);
        }
        catch
        {
            return null;
        }
    }

    private static void Save(string path, LocalApiRuntimeSettings settings)
    {
        var doc = new SettingsDocument
        {
            Port = settings.Port,
            AuthTokenProtected = ProtectToken(settings.AuthToken),
            AuthToken = null,
            TokenProtection = TokenProtectionMode,
            CreatedAtUtc = settings.CreatedAtUtc,
            UpdatedAtUtc = settings.UpdatedAtUtc
        };
        string raw = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, raw);
    }

    private static void SaveEndpoint(int port)
    {
        string path = GetEndpointPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var doc = new EndpointDocument
        {
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        string raw = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, raw);
    }

    private static int FindAvailablePort(int preferred)
    {
        int start = Math.Clamp(preferred, DefaultPort, MaxPort);
        for (int port = start; port <= MaxPort; port++)
        {
            if (IsPortAvailable(port))
            {
                return port;
            }
        }

        for (int port = DefaultPort; port < start; port++)
        {
            if (IsPortAvailable(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("No free localhost port found for PORTA local API.");
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string GetSettingsPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "security", SettingsFileName);
    }

    private static string GetLegacySettingsPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", LegacySettingsFileName);
    }

    private static string GetEndpointPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", EndpointFileName);
    }

    private static string ProtectToken(string token)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI token protection requires Windows.");
        }

        byte[] plain = System.Text.Encoding.UTF8.GetBytes(token);
        byte[] protectedBytes = ProtectedData.Protect(plain, TokenEntropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string? TryUnprotectToken(string? protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(protectedToken);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, TokenEntropy, DataProtectionScope.LocalMachine);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static string ParseLoopbackHost(string? host)
    {
        if (!string.IsNullOrWhiteSpace(host)
            && IPAddress.TryParse(host, out IPAddress? address)
            && IPAddress.IsLoopback(address))
        {
            return address.ToString();
        }

        return IPAddress.Loopback.ToString();
    }

    private sealed class SettingsDocument
    {
        public int Port { get; init; } = DefaultPort;
        public string? AuthTokenProtected { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AuthToken { get; init; }
        public string TokenProtection { get; init; } = TokenProtectionMode;
        public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    private sealed class EndpointDocument
    {
        public string Host { get; init; } = IPAddress.Loopback.ToString();
        public int Port { get; init; } = DefaultPort;
        public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}
