using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Porta.Agent.Common.Environment;

namespace Porta.Agent.Runtime.Render;

internal sealed class VisualSmokeSnapshotExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly HashSet<string> _seenBadges = new(StringComparer.OrdinalIgnoreCase);

    public VisualSmokeSnapshotExporter(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PortaAgent", "visual-smoke-snapshots.ndjson")
            : path;
    }

    public bool TryAppend(RenderSurfaceFrame frame)
    {
        try
        {
            lock (_sync)
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _seenBadges.Add(frame.Overlay.BadgeText);
                var payload = new
                {
                    frame.RenderTimestamp,
                    badge = frame.Overlay.BadgeText,
                    transitionMs = frame.Overlay.TransitionMs,
                    motionVelocity = frame.Motion.MotionVelocity,
                    kelvinShift = frame.Background.KelvinShift
                };
                File.AppendAllText(_path, JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public string BuildSummary()
    {
        lock (_sync)
        {
            bool hasNormal = _seenBadges.Contains("NORMAL");
            bool hasReduced = _seenBadges.Contains("REDUCED");
            bool hasSafe = _seenBadges.Contains("SAFE");
            bool pass = hasNormal && hasReduced && hasSafe;
            return $"visual_smoke pass={pass} normal={hasNormal} reduced={hasReduced} safe={hasSafe} path={_path}";
        }
    }
}
