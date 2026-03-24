namespace Porta.UserAgent;

using System.IO;

internal static class UserAgentFileLog
{
    private static readonly object Gate = new();
    private static readonly string PrimaryLogPath = BuildCommonLogPath();
    private static readonly string FallbackLogPath = BuildUserLogPath();

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
