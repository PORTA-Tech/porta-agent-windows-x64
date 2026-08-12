namespace Porta.UserAgent;

using System.IO;

/// <summary>
/// The user agent's own log.
///
/// <para><b>Maintenance item (b), 2026-08-12 — the path can now be overridden.</b> Until it could,
/// this class had no seam at all, so every test run appended to the LIVE
/// <c>%ProgramData%\PortaAgent\user-agent.log</c>. Bursts of dozens of identical lines in the field
/// log were test runs, not field events, and that has already been read as field data once during
/// an investigation where the log was the only instrument.</para>
///
/// <para>Two ways to redirect, both resolved per write so neither depends on type-initialisation
/// order: the <c>PORTA_USERAGENT_LOG</c> environment variable (which a child process inherits), and
/// <see cref="RedirectTo"/>, which the test assembly calls from a module initializer so that every
/// test in it is redirected before a single line can be written.</para>
/// </summary>
internal static class UserAgentFileLog
{
    /// <summary>Set this and the agent logs there instead. Read once, at type initialisation.</summary>
    public const string LogPathEnvironmentVariable = "PORTA_USERAGENT_LOG";

    private static readonly object Gate = new();
    private static readonly string PrimaryLogPath = BuildCommonLogPath();
    private static readonly string FallbackLogPath = BuildUserLogPath();
    private static string? _overridePath = ReadOverrideFromEnvironment();

    /// <summary>
    /// Sends every subsequent line somewhere else. Called by the test assembly's module initializer;
    /// nothing in the product calls it. Passing null or blank restores the real log.
    /// </summary>
    internal static void RedirectTo(string? path)
    {
        lock (Gate)
        {
            _overridePath = string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }

    /// <summary>Where lines are going right now — the override if one is set, else the real log.</summary>
    internal static string CurrentPath
    {
        get
        {
            lock (Gate)
            {
                return _overridePath ?? PrimaryLogPath;
            }
        }
    }

    private static string? ReadOverrideFromEnvironment()
    {
        try
        {
            string? configured = Environment.GetEnvironmentVariable(LogPathEnvironmentVariable);
            return string.IsNullOrWhiteSpace(configured) ? null : configured;
        }
        catch
        {
            return null;
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Error(string message, Exception? ex)
    {
        Write("ERROR", message, ex);
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                string line = $"[{DateTimeOffset.UtcNow:O}] [{level}] {message}";
                if (ex != null)
                {
                    line += $" :: {ex.GetType().Name}: {ex.Message}";
                }

                string payload = line + Environment.NewLine;
                // An override is absolute: if it cannot be written, the line is dropped rather than
                // falling back to the real log. A redirect that silently leaks into the field log on
                // one bad path would be worse than no redirect at all.
                if (_overridePath is { } redirected)
                {
                    _ = TryAppend(redirected, payload);
                    return;
                }

                if (!TryAppend(PrimaryLogPath, payload))
                {
                    _ = TryAppend(FallbackLogPath, payload);
                }
            }
        }
        catch
        {
        }
    }

    private static bool TryAppend(string path, string payload)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(path, payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCommonLogPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "user-agent.log");
    }

    private static string BuildUserLogPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", "user-agent.log");
    }
}
