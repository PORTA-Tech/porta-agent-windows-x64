using System;
using System.Text;

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

    public static void Event(string name, params (string Key, object? Value)[] fields)
    {
        Write("EVENT", BuildPayload(name, fields));
    }

    public static void Counter(string name, long value, params (string Key, object? Value)[] fields)
    {
        var prefixedFields = new (string Key, object? Value)[fields.Length + 1];
        prefixedFields[0] = ("value", value);
        for (int i = 0; i < fields.Length; i++)
        {
            prefixedFields[i + 1] = fields[i];
        }

        Write("COUNTER", BuildPayload(name, prefixedFields));
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            Console.WriteLine($"[{level}] ts={DateTimeOffset.UtcNow:O} {message}");
        }
    }

    private static string BuildPayload(string name, (string Key, object? Value)[] fields)
    {
        var builder = new StringBuilder();
        builder.Append("name=").Append(name);

        foreach (var (key, value) in fields)
        {
            builder.Append(' ').Append(key).Append('=');
            if (value is null)
            {
                builder.Append("null");
                continue;
            }

            string text = value.ToString() ?? string.Empty;
            if (text.IndexOfAny(new[] { ' ', '"' }) >= 0)
            {
                builder.Append('"').Append(text.Replace("\"", "'")).Append('"');
            }
            else
            {
                builder.Append(text);
            }
        }

        return builder.ToString();
    }
}
