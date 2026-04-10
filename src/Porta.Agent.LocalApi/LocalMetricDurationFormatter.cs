namespace Porta.Agent.LocalApi;

using System.Globalization;

public static class LocalMetricDurationFormatter
{
    public static string FormatMetricDuration(double seconds)
    {
        double normalizedSeconds = double.IsFinite(seconds)
            ? Math.Max(0d, seconds)
            : 0d;
        int totalSeconds = Math.Max(0, (int)Math.Floor(normalizedSeconds));
        int hours = totalSeconds / 3600;
        int remainingSeconds = totalSeconds % 3600;
        int minutes = remainingSeconds / 60;
        int trailingSeconds = remainingSeconds % 60;

        if (hours > 0)
        {
            return trailingSeconds > 0
                ? $"{hours.ToString(CultureInfo.InvariantCulture)}h {minutes.ToString("00", CultureInfo.InvariantCulture)}m {trailingSeconds.ToString("00", CultureInfo.InvariantCulture)}s"
                : $"{hours.ToString(CultureInfo.InvariantCulture)}h {minutes.ToString("00", CultureInfo.InvariantCulture)}m";
        }

        if (minutes > 0)
        {
            return trailingSeconds > 0
                ? $"{minutes.ToString(CultureInfo.InvariantCulture)}m {trailingSeconds.ToString("00", CultureInfo.InvariantCulture)}s"
                : $"{minutes.ToString(CultureInfo.InvariantCulture)}m";
        }

        return $"{totalSeconds.ToString(CultureInfo.InvariantCulture)}s";
    }
}
