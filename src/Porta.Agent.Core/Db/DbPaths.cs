namespace Porta.Agent.Core.Db;

internal static class DbPaths
{
    internal static string GetCoreStateDbPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string directory = Path.Combine(root, "PortaAgent");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "core-state.db");
    }
}
