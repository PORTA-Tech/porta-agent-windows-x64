using System;

namespace Porta.Agent.Runtime.Diagnostics;

internal static class Logger
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(Exception ex, string message)
    {
        Write("ERROR", $"{message} :: {ex.GetType().Name}: {ex.Message}");
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            Console.WriteLine($"[{level}] ts={DateTimeOffset.UtcNow:O} {message}");
        }
    }
}
