using System;
using System.IO;
using System.Text.Json;

namespace Porta.Agent.Service.Fleet;

internal sealed record ControlPlaneReporterState(
    int SchemaVersion,
    long LastSentEventId,
    DateTimeOffset? LastSummarySuccessUtc = null,
    DateTimeOffset? LastActionSuccessUtc = null);

internal sealed class ControlPlaneReporterStateStore
{
    private const int CurrentSchemaVersion = 1;
    private const string FileName = "fleet-reporter-state.v1.json";
    private readonly string _path;

    public ControlPlaneReporterStateStore()
        : this(pathOverride: null)
    {
    }

    internal ControlPlaneReporterStateStore(string? pathOverride)
    {
        _path = ResolvePath(pathOverride);
    }

    public ControlPlaneReporterState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return CreateDefault();
            }

            string json = File.ReadAllText(_path);
            ControlPlaneReporterState? state = JsonSerializer.Deserialize<ControlPlaneReporterState>(json);
            if (state is null || state.SchemaVersion != CurrentSchemaVersion)
            {
                return CreateDefault();
            }

            return state with
            {
                LastSentEventId = Math.Max(0L, state.LastSentEventId),
                LastSummarySuccessUtc = state.LastSummarySuccessUtc?.ToUniversalTime(),
                LastActionSuccessUtc = state.LastActionSuccessUtc?.ToUniversalTime()
            };
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save(ControlPlaneReporterState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Fleet reporter state path must have a parent directory.");
        }

        Directory.CreateDirectory(directory);

        string tempPath = _path + ".tmp";
        ControlPlaneReporterState normalized = state with
        {
            SchemaVersion = CurrentSchemaVersion,
            LastSentEventId = Math.Max(0L, state.LastSentEventId),
            LastSummarySuccessUtc = state.LastSummarySuccessUtc?.ToUniversalTime(),
            LastActionSuccessUtc = state.LastActionSuccessUtc?.ToUniversalTime()
        };

        string json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    private static ControlPlaneReporterState CreateDefault() =>
        new(
            SchemaVersion: CurrentSchemaVersion,
            LastSentEventId: 0L);

    private static string ResolvePath(string? pathOverride)
    {
        if (!string.IsNullOrWhiteSpace(pathOverride))
        {
            return Path.GetFullPath(pathOverride);
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", FileName);
    }
}
