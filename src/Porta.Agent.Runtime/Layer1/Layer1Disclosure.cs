namespace Porta.Agent.Runtime.Layer1;

internal static class Layer1Disclosure
{
    public const string Text =
        "PORTA Agent Layer 1 Disclosure\r\n\r\n"
        + "Collected:\r\n"
        + "- System metrics: CPU%, memory%, network bytes throughput, process count, service health\r\n"
        + "- Interaction aggregates only: keystroke count/window, mouse distance/window, app-switch count/window, idle duration\r\n"
        + "- Agent health: agent CPU%, agent memory, queue depth, error count\r\n\r\n"
        + "Not collected:\r\n"
        + "- No raw text\r\n"
        + "- No key values\r\n"
        + "- No screen pixels\r\n"
        + "- No clipboard data\r\n"
        + "- No file contents\r\n"
        + "- No network payload bodies or endpoints\r\n\r\n"
        + "Purpose:\r\n"
        + "- Privacy-safe signal acquisition only (no normalization, no inference, no policy)\r\n\r\n"
        + "Storage and transport:\r\n"
        + "- Aggregated metrics only are emitted\r\n"
        + "- Raw source details are discarded immediately\r\n\r\n"
        + "Revocation:\r\n"
        + "- Use tray menu: Revoke Consent / Pause / Resume";
}
