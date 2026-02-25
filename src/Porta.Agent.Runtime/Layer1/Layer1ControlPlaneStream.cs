using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Porta.Agent.Runtime.Layer1;

internal sealed class Layer1ControlPlaneStream
{
    public async Task RunAsync(string url, Action<Layer1TrayInterventionView> onIntervention, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(url), token).ConfigureAwait(false);
        byte[] buffer = new byte[8192];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult receive = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (receive.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            string payload = Encoding.UTF8.GetString(buffer, 0, receive.Count);
            foreach (Layer1TrayInterventionView intervention in ParseInterventions(payload))
            {
                onIntervention(intervention);
            }
        }
    }

    private static IEnumerable<Layer1TrayInterventionView> ParseInterventions(string json)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("interventions", out JsonElement interventions)
                || interventions.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (JsonElement item in interventions.EnumerateArray())
            {
                string actionType = item.TryGetProperty("action_type", out JsonElement at) ? at.GetString() ?? "unknown" : "unknown";
                DateTimeOffset start = item.TryGetProperty("start_time", out JsonElement st) && DateTimeOffset.TryParse(st.GetString(), out DateTimeOffset startUtc)
                    ? startUtc
                    : DateTimeOffset.UtcNow;
                DateTimeOffset end = item.TryGetProperty("end_time", out JsonElement et) && DateTimeOffset.TryParse(et.GetString(), out DateTimeOffset endUtc)
                    ? endUtc
                    : start.AddMinutes(5);
                double confidence = item.TryGetProperty("confidence", out JsonElement cf) && cf.TryGetDouble(out double c)
                    ? Math.Clamp(c, 0, 1)
                    : 0;
                string policyId = item.TryGetProperty("policy_id", out JsonElement pid) ? pid.GetString() ?? "unknown-policy" : "unknown-policy";
                string reason = item.TryGetProperty("reason", out JsonElement rs) ? rs.GetString() ?? string.Empty : string.Empty;
                yield return new Layer1TrayInterventionView(actionType, start, end, confidence, policyId, reason);
            }
        }
        finally
        {
            doc?.Dispose();
        }
    }
}
