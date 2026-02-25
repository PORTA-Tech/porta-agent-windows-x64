using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Runtime.Diagnostics;

namespace Porta.Agent.Runtime.Transport;

internal sealed class PortaClient : IDisposable
{
    private sealed class HeartbeatAck
    {
        public int? config_seq { get; init; }
        public string? config_version { get; init; }
        public bool? signature_validity { get; init; }
    }

    private readonly string _agentId = Environment.MachineName;
    private readonly Uri? _baseUri;
    private readonly int _maxRetries;
    private readonly TimeSpan _requestTimeout;
    private readonly string? _pinnedServerCertificateSha256;
    private readonly object _gate = new();
    private HttpClient? _httpClient;
    private int _started;
    private int? _lastConfigSeq;
    private string? _lastConfigVersion;
    private bool? _lastSignatureValidity;

    public PortaClient(string? baseUrl = null, int maxRetries = 3, int timeoutMs = 2000)
    {
        string? configured = baseUrl ?? Environment.GetEnvironmentVariable("PORTA_BACKEND_URL");
        if (!string.IsNullOrWhiteSpace(configured) && Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri))
        {
            _baseUri = uri;
        }

        _maxRetries = Math.Max(1, maxRetries);
        _requestTimeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 250, 10000));
        _pinnedServerCertificateSha256 = NormalizeHex(Environment.GetEnvironmentVariable("PORTA_TLS_CERT_SHA256_PIN"));
    }

    public bool IsEnabled => _baseUri != null;

    public string AgentId => _agentId;

    public Task StartAsync(CancellationToken token)
    {
        if (!IsEnabled || Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        if (!string.Equals(_baseUri!.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _started, 0);
            throw new InvalidOperationException($"TLS required for backend transport. Refusing scheme '{_baseUri.Scheme}'.");
        }

        if (string.IsNullOrWhiteSpace(_agentId))
        {
            Interlocked.Exchange(ref _started, 0);
            throw new InvalidOperationException("Anonymous emission is not allowed: agent identifier missing.");
        }

        lock (_gate)
        {
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(_pinnedServerCertificateSha256))
            {
                handler.ServerCertificateCustomValidationCallback = (_, cert, _, policyErrors) =>
                {
                    if (policyErrors != System.Net.Security.SslPolicyErrors.None || cert == null)
                    {
                        return false;
                    }

                    string presented = NormalizeHex(Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))) ?? string.Empty;
                    return string.Equals(presented, _pinnedServerCertificateSha256, StringComparison.Ordinal);
                };
            }

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = _baseUri,
                Timeout = _requestTimeout
            };
        }

        Logger.Event("transport_started", ("base_url", _baseUri!.ToString()));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            _httpClient?.Dispose();
            _httpClient = null;
        }

        Logger.Event("transport_stopped");
        return Task.CompletedTask;
    }

    public async Task<bool> SendTelemetryBatchAsync(IReadOnlyList<AgentTelemetry> batch, CancellationToken token)
    {
        if (!IsEnabled || batch.Count == 0)
        {
            return false;
        }

        return await ExecuteWithRetryAsync(
            async (client, ct) =>
            {
                using HttpResponseMessage response = await client.PostAsJsonAsync("api/agent/telemetry", batch, ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            },
            "telemetry_batch",
            token).ConfigureAwait(false);
    }

    public async Task<bool> SendHeartbeatAsync(AgentStatus status, ControlParameters control, string? note, CancellationToken token)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var payload = new
        {
            agentId = _agentId,
            timestampUtc = DateTimeOffset.UtcNow,
            status,
            control,
            note
        };

        return await ExecuteWithRetryAsync(
            async (client, ct) =>
            {
                using HttpResponseMessage response = await client.PostAsJsonAsync("api/agent/heartbeat", payload, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                HeartbeatAck? ack = await response.Content.ReadFromJsonAsync<HeartbeatAck>(cancellationToken: ct).ConfigureAwait(false);
                if (ack?.config_seq is int configSeq)
                {
                    bool changed;
                    lock (_gate)
                    {
                        changed =
                            _lastConfigSeq != configSeq
                            || _lastConfigVersion != ack.config_version
                            || _lastSignatureValidity != ack.signature_validity;
                        _lastConfigSeq = configSeq;
                        _lastConfigVersion = ack.config_version;
                        _lastSignatureValidity = ack.signature_validity;
                    }

                    if (changed)
                    {
                        Logger.Event(
                            "control_plane_config_ack",
                            ("config_seq", configSeq),
                            ("config_version", ack.config_version ?? string.Empty),
                            ("signature_validity", ack.signature_validity?.ToString() ?? "null"));
                    }
                }

                return true;
            },
            "heartbeat",
            token).ConfigureAwait(false);
    }

    private async Task<bool> ExecuteWithRetryAsync(
        Func<HttpClient, CancellationToken, Task<bool>> operation,
        string operationName,
        CancellationToken token)
    {
        HttpClient? client;
        lock (_gate)
        {
            client = _httpClient;
        }

        if (client == null)
        {
            return false;
        }

        int delayMs = 250;
        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                bool ok = await operation(client, token).ConfigureAwait(false);
                if (ok)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Logger.Warn($"Transport timeout for {operationName} attempt={attempt}.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Transport error for {operationName} attempt={attempt}.");
            }

            if (attempt < _maxRetries)
            {
                await Task.Delay(delayMs, token).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 2000);
            }
        }

        return false;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
    }
}
