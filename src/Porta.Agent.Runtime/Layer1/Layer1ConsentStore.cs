using System.Text.Json;

namespace Porta.Agent.Runtime.Layer1;

internal sealed class Layer1ConsentStore
{
    private const string DefaultFileName = "consent-state.json";
    private readonly string _path;

    private sealed class ConsentDocument
    {
        public string State { get; init; } = nameof(Layer1ConsentState.InstalledNotConsented);
        public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }

    public Layer1ConsentStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? BuildDefaultPath()
            : path!;
    }

    public Layer1ConsentState Load()
    {
        return Load(out _, out _);
    }

    public Layer1ConsentState Load(out bool stateFileMissing, out bool stateCorrupted)
    {
        stateFileMissing = false;
        stateCorrupted = false;
        try
        {
            if (!File.Exists(_path))
            {
                stateFileMissing = true;
                return Layer1ConsentState.NotInstalled;
            }

            string raw = File.ReadAllText(_path);
            ConsentDocument? doc = JsonSerializer.Deserialize<ConsentDocument>(raw);
            if (doc == null)
            {
                stateCorrupted = true;
                return Layer1ConsentState.InstalledNotConsented;
            }

            Layer1ConsentState parsed = ParseState(doc.State, out bool parsedOk);
            if (!parsedOk)
            {
                stateCorrupted = true;
            }
            return parsed;
        }
        catch
        {
            stateCorrupted = true;
            return Layer1ConsentState.InstalledNotConsented;
        }
    }

    public void Save(Layer1ConsentState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var doc = new ConsentDocument
        {
            State = state.ToString(),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        string raw = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, raw);
    }

    private static Layer1ConsentState ParseState(string raw, out bool parsedOk)
    {
        if (Enum.TryParse<Layer1ConsentState>(raw, ignoreCase: true, out Layer1ConsentState parsed))
        {
            parsedOk = true;
            return parsed;
        }

        parsedOk = false;
        return Layer1ConsentState.InstalledNotConsented;
    }

    private static string BuildDefaultPath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "Porta", "Layer1", DefaultFileName);
    }
}
