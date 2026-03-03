using System.Globalization;

namespace Porta.Agent.Core;

public static class RawInputContributors
{
    public const string Prefix = "raw_input:";

    public static string KeyboardKeydown(bool isBackspace)
    {
        return isBackspace ? "raw_input:keyboard:backspace" : "raw_input:keyboard:keydown";
    }

    public static string MouseMove(int dx, int dy)
    {
        return FormattableString.Invariant($"raw_input:mouse_move:{dx}:{dy}");
    }

    public static string MouseWheel(int delta)
    {
        return FormattableString.Invariant($"raw_input:mouse_wheel:{delta}");
    }

    public static string Focus(string appHash)
    {
        string normalized = string.IsNullOrWhiteSpace(appHash)
            ? "unknown"
            : appHash.Trim().ToLowerInvariant();
        return $"raw_input:focus:{normalized}";
    }

    public static string Idle(double idleSeconds)
    {
        double normalized = (!double.IsFinite(idleSeconds) || idleSeconds < 0d) ? 0d : idleSeconds;
        return $"raw_input:idle:{normalized.ToString("F3", CultureInfo.InvariantCulture)}";
    }
}
