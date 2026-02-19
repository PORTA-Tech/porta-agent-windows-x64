using System;
using System.IO;
using System.Text.Json;
using Porta.Agent.Common.Environment;

namespace Porta.Agent.Runtime.Render;

internal sealed class RenderFrameStreamExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _fallbackPath;

    public RenderFrameStreamExporter(string? path = null)
    {
        string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localRoot))
        {
            localRoot = AppContext.BaseDirectory;
        }

        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(localRoot, "PortaAgent", "render-frame-stream.ndjson")
            : path;
        _fallbackPath = Path.Combine(AppContext.BaseDirectory, "render-frame-stream.ndjson");
    }

    public bool TryAppend(RenderSurfaceFrame frame)
    {
        string line = BuildLine(frame);
        try
        {
            lock (_sync)
            {
                if (TryWriteLine(_path, line))
                {
                    return true;
                }

                return TryWriteLine(_fallbackPath, line);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWriteLine(string path, string line)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, line);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildLine(RenderSurfaceFrame frame)
    {
        var payload = new
        {
            schemaVersion = 1,
            frame.RenderTimestamp,
            background = new
            {
                top = frame.Background.TopColor,
                bottom = frame.Background.BottomColor,
                frame.Background.BrightnessFactor,
                frame.Background.HighlightTint,
                frame.Background.ShadowTint,
                frame.Background.SaturationScale,
                frame.Background.KelvinShift
            },
            motion = new
            {
                frame.Motion.ParticleDensity,
                frame.Motion.MotionVelocity,
                frame.Motion.NoiseAmplitude,
                frame.Motion.LoopCadenceHz
            },
            depth = new
            {
                frame.Depth.ParallaxAmount,
                frame.Depth.VignetteStrength,
                frame.Depth.Clarity
            },
            overlay = new
            {
                frame.Overlay.BadgeText,
                authority = frame.Overlay.AuthorityLevel.ToString().ToLowerInvariant(),
                frame.Overlay.ReducedAnimation,
                frame.Overlay.EasingAggressiveness,
                frame.Overlay.TransitionMs
            }
        };

        return JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine;
    }
}
