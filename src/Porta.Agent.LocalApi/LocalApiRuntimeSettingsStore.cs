using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Porta.Agent.Core.Security;

namespace Porta.Agent.LocalApi;

public sealed record LocalApiRuntimeSettings(
    int Port,
    string? AuthToken,
    bool LocalApiAuthReady,
    string? LocalApiAuthError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record LocalApiEndpointSettings(
    string Host,
    int Port,
    DateTimeOffset UpdatedAtUtc);

[SupportedOSPlatform("windows")]
public static class LocalApiRuntimeSettingsStore
{
    private const int DefaultPort = 17887;
    private const int MaxPort = 17950;
    private const string SettingsFileName = "local-api.runtime.json";
    private const string LegacySettingsFileName = "local-api.json";
    private const string EndpointFileName = "local-api.endpoint.json";

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

        int port = FindAvailablePort(preferredPort ?? existing?.Port ?? LoadEndpoint()?.Port ?? DefaultPort);
        Secrets.ApiTokenReadResult tokenResult = Secrets.GetApiToken();
        string? token = tokenResult.Success && tokenResult.TokenBytes != null
            ? Convert.ToHexString(tokenResult.TokenBytes)
            : null;
        var settings = new LocalApiRuntimeSettings(
            Port: port,
            AuthToken: token,
            LocalApiAuthReady: tokenResult.Success,
            LocalApiAuthError: tokenResult.Success ? null : "TOKEN_READ_FAILED",
            CreatedAtUtc: existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        if (!settings.LocalApiAuthReady)
        {
            Console.Error.WriteLine($"local_api_auth_degraded code={settings.LocalApiAuthError}");
        }

        Save(path, settings);
        SaveEndpoint(settings.Port);
        return settings;
    }

    public static LocalApiRuntimeSettings? Load()
    {
        return TryLoad(GetSettingsPath())
            ?? TryLoad(GetLegacySettingsPath());
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

            Secrets.ApiTokenReadResult tokenResult = Secrets.GetApiToken();
            string? token = tokenResult.Success && tokenResult.TokenBytes != null
                ? Convert.ToHexString(tokenResult.TokenBytes)
                : null;

            return new LocalApiRuntimeSettings(
                Port: doc.Port,
                AuthToken: token,
                LocalApiAuthReady: tokenResult.Success,
                LocalApiAuthError: tokenResult.Success ? null : "TOKEN_READ_FAILED",
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
