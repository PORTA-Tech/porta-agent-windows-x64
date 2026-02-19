using System;
using System.IO;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Runtime.Diagnostics;

namespace Porta.Agent.Runtime;

internal sealed class ControlSnapshotExporter
{
    private readonly string _path;
    private readonly object _gate = new();

    public ControlSnapshotExporter(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PortaAgent",
                "control-snapshots.ndjson")
            : path;
    }

    public bool TryAppend(ControlSnapshotTelemetry snapshot)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string line = snapshot.ToCanonicalJson() + Environment.NewLine;
            lock (_gate)
            {
                File.AppendAllText(_path, line);
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Control snapshot export skipped: {ex.Message}");
            return false;
        }
    }
}
