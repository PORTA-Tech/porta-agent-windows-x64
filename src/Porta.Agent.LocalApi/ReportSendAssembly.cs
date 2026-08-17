using System.IO;
using System.Text.Json;
using Porta.Agent.Core;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.LocalApi;

/// <summary>
/// Step 10 phase C/D — turning a request from the confirm screen into the approved payload, with
/// every rule applied on the way.
///
/// This is the one place a report becomes sendable, so it is the one place the preflight runs. The
/// preview route and the send route both go through it, which is why a preview cannot show
/// something the send would refuse.
/// </summary>
internal sealed record ReportSendAssembly(
    bool Ok,
    string? Reason,
    IReadOnlyList<string>? Refusals,
    string? Json,
    string? ReportId,
    string? RecipientEmail,
    AgentSentReport? Record);

internal static partial class LocalApiEndpointMapping_ReportAssembly
{
}

/// <summary>
/// The preview file. Written to a temp path, opened in the browser, and deleted afterwards.
///
/// <b>It holds a client's name, their labels and their hours</b>, so it does not get to sit in the
/// temp directory indefinitely. Old previews are swept on every write, which also covers the case
/// where the process died before it could delete its own.
/// </summary>
internal static class ReportPreviewFile
{
    private const string FolderName = "porta-report-preview";

    public static string Write(string html)
    {
        string dir = Path.Combine(Path.GetTempPath(), FolderName);
        Directory.CreateDirectory(dir);

        SweepOld(dir);

        string path = Path.Combine(dir, $"report-{Guid.NewGuid():N}.html");
        File.WriteAllText(path, html);
        return path;
    }

    /// <summary>
    /// Anything older than an hour, on the assumption a preview is looked at within minutes. Errors
    /// are swallowed: failing to sweep must never stop a user previewing a report.
    /// </summary>
    private static void SweepOld(string dir)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (string file in Directory.EnumerateFiles(dir, "report-*.html"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // In use, or already gone. Either way, not this call's problem.
                }
            }
        }
        catch
        {
            // The sweep is hygiene, not correctness.
        }
    }

    /// <summary>Delete one preview now, once it has been opened.</summary>
    public static void Delete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The sweep on the next write will get it.
        }
    }
}
