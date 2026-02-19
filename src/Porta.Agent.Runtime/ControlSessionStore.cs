using System;
using System.IO;
using System.Text.Json;
using Porta.Agent.Common.State;
using Porta.Agent.Runtime.Diagnostics;

namespace Porta.Agent.Runtime;

internal sealed class ControlSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly string _backupPath;

    public ControlSessionStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PortaAgent",
                "control-session.json")
            : path;
        _backupPath = $"{_path}.bak";
    }

    public bool TryLoad(out SessionState? session)
    {
        session = null;
        try
        {
            if (!TryLoadFromFile(_path, out session) && !TryLoadFromFile(_backupPath, out session))
            {
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Session restore skipped: {ex.Message}");
            return false;
        }
    }

    public bool TrySave(SessionState session)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempPath = $"{_path}.tmp";
            string json = JsonSerializer.Serialize(session, JsonOptions);
            File.WriteAllText(tempPath, json);

            if (File.Exists(_path))
            {
                File.Replace(tempPath, _path, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _path);
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Session save skipped: {ex.Message}");
            return false;
        }
    }

    private static bool TryLoadFromFile(string path, out SessionState? session)
    {
        session = null;
        if (!File.Exists(path))
        {
            return false;
        }

        string json = File.ReadAllText(path);
        session = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
        return session != null;
    }
}
