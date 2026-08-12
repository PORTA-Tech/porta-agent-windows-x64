using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Porta.Agent.Core;

namespace Porta.Agent.LocalApi;

[SupportedOSPlatform("windows")]
public sealed class LocalApiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    // The narrative route is the ONE local call that does not end at local SQLite: the local API
    // forwards it to the control plane, which calls Opus. 3s is right for every other route and
    // catastrophically wrong for this one — it hung up ~3s into a call the host allows 10s and the
    // server allows 8-9s, so a line could only land if the whole chain beat 3 seconds. That is the
    // ~7-in-10 narrative failure, and it read as a server problem for four rounds of investigation
    // because the agent never recorded which budget expired (2026-08-08).
    //
    // 12s sits ABOVE the host's 10s cancellation token deliberately: the host budget is meant to be
    // the ceiling, per the documented 10s -> 9s -> 8s chain. This one must never be the binding
    // constraint again. Waiting costs the card nothing — the card renders instantly with its
    // template and the line upgrades it in place whenever it arrives.
    private static readonly TimeSpan NarrativeRequestTimeout = TimeSpan.FromSeconds(12);

    private const string TokenHeader = "X-PORTA-TOKEN";
    private readonly HttpClient _httpClient;
    private readonly HttpClient _narrativeHttpClient;
    private readonly LocalApiRuntimeSettings? _settingsOverride;
    private readonly LocalApiEndpointSettings? _endpointOverride;
    private LocalApiRuntimeSettings? _settings;
    private string? _authToken;
    private string _host = "127.0.0.1";
    private int _port;
    public int? LastStatusHttpCode { get; private set; }
    public DateTimeOffset? LastStatusProbeUtc { get; private set; }
    public int? LastSnapshotHttpCode { get; private set; }
    public DateTimeOffset? LastSnapshotProbeUtc { get; private set; }
    public int? LastHealthHttpCode { get; private set; }
    public DateTimeOffset? LastHealthProbeUtc { get; private set; }

    public LocalApiClient()
        : this(httpClient: null, settingsOverride: null, endpointOverride: null)
    {
    }

    internal LocalApiClient(
        HttpClient? httpClient,
        LocalApiRuntimeSettings? settingsOverride,
        LocalApiEndpointSettings? endpointOverride)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = RequestTimeout
        };
        // An injected client (tests) is reused for both, so the narrative route is exercised
        // through the same stub handler rather than quietly bypassing it.
        _narrativeHttpClient = httpClient ?? new HttpClient
        {
            Timeout = NarrativeRequestTimeout
        };
        _settingsOverride = settingsOverride;
        _endpointOverride = endpointOverride;
        _settings = settingsOverride ?? LocalApiRuntimeSettingsStore.Load();
        _ = RefreshEndpointBinding();
    }

    public bool IsConfigured => HasRuntimeSettings();

    public async Task<LocalApiStatusResponse?> GetStatusAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            LastStatusProbeUtc = DateTimeOffset.UtcNow;
            LastStatusHttpCode = null;
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            LastStatusProbeUtc = DateTimeOffset.UtcNow;
            using HttpResponseMessage response = await _httpClient.GetAsync("/v1/status", token).ConfigureAwait(false);
            LastStatusHttpCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiStatusResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            LastStatusProbeUtc = DateTimeOffset.UtcNow;
            LastStatusHttpCode = null;
            return null;
        }
    }

    public async Task<LocalApiCurrentSnapshotResponse?> GetCurrentSnapshotAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            LastSnapshotProbeUtc = DateTimeOffset.UtcNow;
            LastSnapshotHttpCode = null;
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            LastSnapshotProbeUtc = DateTimeOffset.UtcNow;
            using HttpResponseMessage response = await _httpClient.GetAsync("/v1/snapshot/current", token).ConfigureAwait(false);
            LastSnapshotHttpCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiCurrentSnapshotResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            LastSnapshotProbeUtc = DateTimeOffset.UtcNow;
            LastSnapshotHttpCode = null;
            return null;
        }
    }

    public async Task<LocalApiHealthResponse?> GetHealthAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            LastHealthProbeUtc = DateTimeOffset.UtcNow;
            LastHealthHttpCode = null;
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            LastHealthProbeUtc = DateTimeOffset.UtcNow;
            using HttpResponseMessage response = await _httpClient.GetAsync("/healthz", token).ConfigureAwait(false);
            LastHealthHttpCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiHealthResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            LastHealthProbeUtc = DateTimeOffset.UtcNow;
            LastHealthHttpCode = null;
            return null;
        }
    }

    public async Task<LocalApiLiveSessionStateResponse?> GetLiveSessionStateAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiLiveSessionStateResponse>("/v1/session/live", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiAriContextResponse?> GetAriContextAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiAriContextResponse>("/v1/session/ari-context", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiInputsSummaryResponse?> GetInputsSummaryAsync(int windowSeconds = 60, CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            int window = Math.Clamp(windowSeconds, 5, 3600);
            return await _httpClient.GetFromJsonAsync<LocalApiInputsSummaryResponse>($"/v1/inputs/summary?window={window}", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // Phase 1b — Step 4: local readout of accumulated ground-truth labels (counts only).
    public async Task<LocalApiLabelDiagnosticsResponse?> GetLabelDiagnosticsAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiLabelDiagnosticsResponse>(
                "/v1/labels/diagnostics", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // Phase 1b — Step 3c: ground-truth label prompt (widget pill).
    public async Task<LocalApiLabelPromptDueResponse?> GetLabelPromptDueAsync(
        bool meetingInProgress = false,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            string meeting = meetingInProgress ? "true" : "false";
            return await _httpClient.GetFromJsonAsync<LocalApiLabelPromptDueResponse>(
                $"/v1/labels/prompt-due?meeting={meeting}", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // Phase 1c — signup nudge: ask the core whether an unlinked device is due to be nudged to sign in.
    public async Task<LocalApiSignupNudgeDueResponse?> GetSignupNudgeDueAsync(
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiSignupNudgeDueResponse>(
                "/v1/nudges/signup-due", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // ── Phase 2c: reminders ─────────────────────────────────────────────────────
    public async Task<LocalApiNextReminderResponse?> GetNextReminderAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiNextReminderResponse>(
                "/v1/reminders/next", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiReminderCreateResponse?> CreateReminderAsync(
        LocalApiReminderCreateRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/reminders", content, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LocalApiReminderCreateResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiAckResponse?> AckReminderAsync(
        LocalApiReminderAckRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/reminders/ack", content, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LocalApiAckResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // ── Workstream 4: timed tasks ───────────────────────────────────────────────
    /// <param name="goalDaysBack">
    /// How many goal-days of CLOSED tasks to include as well as today. 0 = today only, which is
    /// what every caller before step 8 wanted. Open tasks always come back regardless.
    /// </param>
    public async Task<LocalApiTasksTodayResponse?> GetTasksTodayAsync(
        CancellationToken token = default,
        int goalDaysBack = 0)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiTasksTodayResponse>(
                goalDaysBack > 0 ? $"/v1/tasks/today?goalDaysBack={goalDaysBack}" : "/v1/tasks/today",
                token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiTaskCreateResponse?> CreateTaskAsync(
        LocalApiTaskCreateRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/tasks", content, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LocalApiTaskCreateResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiTaskActResponse?> ActOnTaskAsync(
        LocalApiTaskActRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/tasks/act", content, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LocalApiTaskActResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // ── Step 9a — projects (local only) ──

    public async Task<LocalApiProjectsResponse?> GetProjectsAsync(
        bool includeArchived = false,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiProjectsResponse>(
                includeArchived ? "/v1/projects?includeArchived=true" : "/v1/projects",
                token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiProjectResponse?> CreateProjectAsync(
        LocalApiProjectCreateRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/projects", content, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LocalApiProjectResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiProjectResponse?> UpdateProjectAsync(
        LocalApiProjectUpdateRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/projects/update", content, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LocalApiProjectResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiTaskActResponse?> SetTaskProjectAsync(
        LocalApiTaskProjectRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/tasks/project", content, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LocalApiTaskActResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // ── Workstream D — conversational check-in goals ──

    public async Task<LocalApiDailyGoalResponse?> GetTodayGoalAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiDailyGoalResponse>("/v1/goals/today", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task<LocalApiDailyGoalResponse?> SetMorningGoalAsync(LocalApiGoalMorningRequest request, CancellationToken token = default)
        => PostGoalAsync("/v1/goals/morning", request, token);

    public Task<LocalApiDailyGoalResponse?> RecordMiddayAnswerAsync(LocalApiGoalMiddayRequest request, CancellationToken token = default)
        => PostGoalAsync("/v1/goals/midday", request, token);

    public Task<LocalApiDailyGoalResponse?> RecordEndOfDayAsync(LocalApiGoalEndOfDayRequest request, CancellationToken token = default)
        => PostGoalAsync("/v1/goals/eod", request, token);

    // Part 5 — one narrative line per high-value moment. Null on any failure; the caller renders
    // its honest template instead, so this can never delay a card past the caller's own budget.
    public async Task<LocalApiNarrativeResponse?> GetNarrativeAsync(
        LocalApiNarrativeRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return NoLine("local_api_not_configured");
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _narrativeHttpClient
                .PostAsync("/v1/narrative", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // No EnsureSuccessStatusCode above on purpose: a non-2xx used to fall into
                // ReadFromJsonAsync, throw on the body, and land in the blind catch as a bare null.
                return NoLine($"local_api_http_{(int)response.StatusCode}");
            }

            return await response.Content.ReadFromJsonAsync<LocalApiNarrativeResponse>(cancellationToken: token)
                .ConfigureAwait(false) ?? NoLine("local_api_empty_body");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The caller gave up (its own budget, or shutdown). Propagate so the host reports its
            // own budget rather than blaming the local API for a decision it made itself.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Not the caller's token, so this is HttpClient.Timeout. Read the budget off the client
            // that actually expired rather than a constant, so the log can never name a number this
            // route was not really running under — that mismatch is what hid the bug.
            return NoLine($"local_api_timeout:{_narrativeHttpClient.Timeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            return NoLine($"local_api_transport:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// A "there is no line, and here is why" response. This route is the one place a bare null was
    /// load-bearing information: every other layer of the narrative chain already hands its reason
    /// code up (ControlPlaneClient, NarrativeGateway), and this catch swallowed the last one — so a
    /// failure on this machine was indistinguishable from a model that declined on the server.
    /// The caller reads Reason straight through to user-agent.log.
    /// </summary>
    private static LocalApiNarrativeResponse NoLine(string reason)
        => new(Success: false, Line: null, TimestampUtc: DateTimeOffset.UtcNow, Reason: reason);

    private async Task<LocalApiDailyGoalResponse?> PostGoalAsync(string path, object request, CancellationToken token)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(path, content, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LocalApiDailyGoalResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiAckResponse?> RecordFocusLabelAsync(
        LocalApiFocusLabelRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        if (request.SchemaVer != 1)
        {
            return new LocalApiAckResponse(
                Success: false,
                Message: "invalid_schema_ver",
                TimestampUtc: DateTimeOffset.UtcNow);
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/labels/focus", content, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LocalApiAckResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiAckResponse?> SendInputSignalsAsync(
        LocalApiInputSignalsRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        if (request.SchemaVer is not 1 and not 2)
        {
            return new LocalApiAckResponse(
                Success: false,
                Message: "invalid_schema_ver",
                TimestampUtc: DateTimeOffset.UtcNow);
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/input/signals", content, token).ConfigureAwait(false);
            return await ReadInputSignalsAckWithDiagnosticsAsync(response, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            return new LocalApiAckResponse(
                Success: false,
                Message: BuildInputSignalsFailureMessage("timeout", statusCode: null, ackMessage: null, rawBody: null, detail: ex.Message),
                TimestampUtc: DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            return new LocalApiAckResponse(
                Success: false,
                Message: BuildInputSignalsFailureMessage("transport_error", statusCode: null, ackMessage: null, rawBody: null, detail: ex.Message),
                TimestampUtc: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new LocalApiAckResponse(
                Success: false,
                Message: BuildInputSignalsFailureMessage("client_error", statusCode: null, ackMessage: null, rawBody: null, detail: $"{ex.GetType().Name}: {ex.Message}"),
                TimestampUtc: DateTimeOffset.UtcNow);
        }
    }

    public async Task<LocalApiAckResponse?> SendLiveSessionStateAsync(
        LocalApiLiveSessionStateRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        if (request.SchemaVer != 1)
        {
            return new LocalApiAckResponse(
                Success: false,
                Message: "invalid_schema_ver",
                TimestampUtc: DateTimeOffset.UtcNow);
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/session/live", content, token).ConfigureAwait(false);
            LocalApiAckResponse? payload = await ReadAckResponseAsync(response, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return payload ?? new LocalApiAckResponse(
                    Success: false,
                    Message: $"http_{(int)response.StatusCode}",
                    TimestampUtc: DateTimeOffset.UtcNow);
            }

            return payload;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<LocalApiEventResponse>> GetRecentEventsAsync(int limit = 50, DateTimeOffset? sinceUtc = null, CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return [];
        }

        try
        {
            RefreshEndpointBinding();
            int boundedLimit = Math.Clamp(limit, 1, 500);
            string path = $"/v1/events/recent?limit={boundedLimit}";
            if (sinceUtc.HasValue)
            {
                string encoded = Uri.EscapeDataString(sinceUtc.Value.ToString("O"));
                path = $"{path}&since={encoded}";
            }

            IReadOnlyList<LocalApiEventResponse>? response = await _httpClient.GetFromJsonAsync<IReadOnlyList<LocalApiEventResponse>>(path, token).ConfigureAwait(false);
            return response ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<LocalApiEventResponse>> GetEventsSinceIdAsync(long sinceIdExclusive, int limit = 200, CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return [];
        }

        try
        {
            RefreshEndpointBinding();
            int boundedLimit = Math.Clamp(limit, 1, 1000);
            long safeSinceId = Math.Max(0, sinceIdExclusive);
            string path = $"/v1/events?since_id={safeSinceId}&limit={boundedLimit}";
            IReadOnlyList<LocalApiEventResponse>? response =
                await _httpClient.GetFromJsonAsync<IReadOnlyList<LocalApiEventResponse>>(path, token).ConfigureAwait(false);
            return response ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<LocalApiTimelineResponse?> GetTimelineAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            int bucket = Math.Clamp(bucketSeconds, 1, 3600);
            string from = Uri.EscapeDataString(fromUtc.ToString("O"));
            string to = Uri.EscapeDataString(toUtc.ToString("O"));
            string path = $"/v1/timeline?from={from}&to={to}&bucketSeconds={bucket}";
            return await _httpClient.GetFromJsonAsync<LocalApiTimelineResponse>(path, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiSettingsResponse?> GetEffectiveSettingsAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiSettingsResponse>("/v1/settings/effective", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiRetentionResponse?> GetDataRetentionAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiRetentionResponse>("/v1/data/retention", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiIdentityLinkResponse?> GetIdentityLinkAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiIdentityLinkResponse>("/v1/identity/link", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiIdentityLinkResponse?> SetIdentityLinkAsync(
        string accountId,
        string orgId,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(orgId))
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            var request = new LocalApiSetIdentityLinkRequest(accountId.Trim(), orgId.Trim());
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/identity/link", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiIdentityLinkResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> SetDeviceCredentialAsync(
        string deviceId,
        string deviceSecret,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceSecret))
        {
            return false;
        }

        try
        {
            RefreshEndpointBinding();
            var request = new LocalApiSetDeviceCredentialRequest(deviceId.Trim(), deviceSecret.Trim());
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/identity/device-credential", content, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<LocalApiDeviceCredentialStatusResponse?> GetDeviceCredentialStatusAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using HttpResponseMessage response = await _httpClient.GetAsync("/v1/identity/device-credential/status", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiDeviceCredentialStatusResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiIdentityLinkResponse?> ClearIdentityLinkAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/identity/unlink", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiIdentityLinkResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task<bool> PauseAsync(string? reason = null, DateTimeOffset? untilUtc = null, CancellationToken token = default)
    {
        return PostAsync("/v1/control/pause", new LocalApiPauseRequest(reason, untilUtc), token);
    }

    public Task<bool> ResumeAsync(CancellationToken token = default)
    {
        return PostAsync("/v1/control/resume", null, token);
    }

    public Task<bool> GrantConsentAsync(CancellationToken token = default)
    {
        return PostAsync("/v1/consent/grant", null, token);
    }

    public Task<bool> RevokeConsentAsync(CancellationToken token = default)
    {
        return PostAsync("/v1/consent/revoke", null, token);
    }

    public async Task<LocalApiSettingsResponse?> UpdateSettingsAsync(LocalApiUpdateSettingsRequest request, CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/settings/update", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiSettingsResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiRecalibrationResponse?> RecalibrateBaselineAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/baseline/recalibrate", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiRecalibrationResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiExportResponse?> ExportDataAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/data/export", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiExportResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task<bool> DeleteLocalDataAsync(CancellationToken token = default)
    {
        return PostAsync("/v1/data/delete_local", null, token);
    }

    public async Task<LocalApiAckResponse?> RestartServiceAsync(CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/service/restart", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiAckResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiAckResponse?> TriggerTestInterventionAsync(string action, CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            return new LocalApiAckResponse(
                Success: false,
                Message: "missing_action",
                TimestampUtc: DateTimeOffset.UtcNow);
        }

        try
        {
            RefreshEndpointBinding();
            var request = new LocalApiTestInterventionRequest(action.Trim());
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/testing/intervention", content, token).ConfigureAwait(false);
            LocalApiAckResponse? payload = await response.Content.ReadFromJsonAsync<LocalApiAckResponse>(cancellationToken: token).ConfigureAwait(false);
            return payload ?? new LocalApiAckResponse(
                Success: false,
                Message: response.IsSuccessStatusCode ? "empty_response" : $"http_{(int)response.StatusCode}",
                TimestampUtc: DateTimeOffset.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<LocalApiInterventionPendingResponse>> GetPendingInterventionsAsync(
        string sessionId,
        int limit = 20,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings() || string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        try
        {
            RefreshEndpointBinding();
            string path = $"/v1/interventions/pending?session_id={Uri.EscapeDataString(sessionId.Trim())}&limit={Math.Clamp(limit, 1, 50)}";
            using HttpResponseMessage response = await _httpClient.GetAsync(path, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            IReadOnlyList<LocalApiInterventionPendingResponse>? payload =
                await response.Content.ReadFromJsonAsync<IReadOnlyList<LocalApiInterventionPendingResponse>>(cancellationToken: token)
                    .ConfigureAwait(false);
            return payload ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<LocalApiAckResponse?> AcknowledgeInterventionAsync(
        LocalApiInterventionAckRequest request,
        CancellationToken token = default)
    {
        if (!HasRuntimeSettings())
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/interventions/ack", content, token).ConfigureAwait(false);
            LocalApiAckResponse? payload = await ReadAckResponseAsync(response, token).ConfigureAwait(false);
            return payload ?? new LocalApiAckResponse(
                Success: false,
                Message: response.IsSuccessStatusCode ? "empty_response" : $"http_{(int)response.StatusCode}",
                TimestampUtc: DateTimeOffset.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> PostAsync(string path, object? payload, CancellationToken token)
    {
        if (!HasRuntimeSettings())
        {
            return false;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = payload == null
                ? new StringContent(string.Empty, Encoding.UTF8, "application/json")
                : new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(path, content, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        if (!ReferenceEquals(_narrativeHttpClient, _httpClient))
        {
            _narrativeHttpClient.Dispose();
        }
    }

    private static async Task<LocalApiAckResponse?> ReadAckResponseAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<LocalApiAckResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            string raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return new LocalApiAckResponse(
                Success: false,
                Message: response.IsSuccessStatusCode ? "invalid_ack_payload" : $"http_{(int)response.StatusCode}",
                TimestampUtc: DateTimeOffset.UtcNow);
        }
    }

    private static async Task<LocalApiAckResponse> ReadInputSignalsAckWithDiagnosticsAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        string? rawBody = null;
        try
        {
            rawBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rawBody = $"<read_failed:{ex.GetType().Name}>";
        }

        LocalApiAckResponse? payload = TryDeserializeAck(rawBody);
        if (response.IsSuccessStatusCode)
        {
            return payload ?? new LocalApiAckResponse(
                Success: false,
                Message: BuildInputSignalsFailureMessage("invalid_ack_payload", (int)response.StatusCode, ackMessage: null, rawBody, detail: null),
                TimestampUtc: DateTimeOffset.UtcNow);
        }

        string failureKind = (int)response.StatusCode >= 500 ? "server_error" : "http_error";
        return new LocalApiAckResponse(
            Success: false,
            Message: BuildInputSignalsFailureMessage(failureKind, (int)response.StatusCode, payload?.Message, rawBody, detail: null),
            TimestampUtc: payload?.TimestampUtc ?? DateTimeOffset.UtcNow);
    }

    private static LocalApiAckResponse? TryDeserializeAck(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LocalApiAckResponse>(rawBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static string BuildInputSignalsFailureMessage(
        string failureKind,
        int? statusCode,
        string? ackMessage,
        string? rawBody,
        string? detail)
    {
        var builder = new StringBuilder();
        builder.Append("input_publish_failed kind=").Append(failureKind);
        if (statusCode.HasValue)
        {
            builder.Append(" status=").Append(statusCode.Value);
        }

        if (!string.IsNullOrWhiteSpace(ackMessage))
        {
            builder.Append(" ack=").Append(CompactDiagnosticValue(ackMessage));
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            builder.Append(" detail=").Append(CompactDiagnosticValue(detail));
        }

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            builder.Append(" body=").Append(CompactDiagnosticValue(rawBody));
        }

        return builder.ToString();
    }

    private static string CompactDiagnosticValue(string value)
    {
        string compact = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        if (compact.Length > 300)
        {
            compact = compact[..300];
        }

        return compact;
    }

    private bool HasRuntimeSettings()
    {
        return RefreshEndpointBinding();
    }

    // Every client this binds must stay in lockstep: a narrative client left without the auth token
    // or the base address would fail in a way that looks exactly like the bug this pair was split
    // to fix. Iterating them is what keeps that true by construction rather than by memory.
    private IEnumerable<HttpClient> BoundClients()
    {
        yield return _httpClient;
        if (!ReferenceEquals(_narrativeHttpClient, _httpClient))
        {
            yield return _narrativeHttpClient;
        }
    }

    private bool RefreshEndpointBinding()
    {
        _settings = _settingsOverride ?? LocalApiRuntimeSettingsStore.Load();
        if (_settings == null)
        {
            _authToken = null;
            foreach (HttpClient client in BoundClients())
            {
                client.DefaultRequestHeaders.Remove(TokenHeader);
            }

            return false;
        }

        LocalApiEndpointSettings? endpoint = _endpointOverride ?? LocalApiRuntimeSettingsStore.LoadEndpoint();
        int nextPort = endpoint?.Port ?? _settings.Port;
        string nextHost = endpoint?.Host ?? "127.0.0.1";
        string? nextAuthToken = string.IsNullOrWhiteSpace(_settings.AuthToken)
            ? null
            : _settings.AuthToken.Trim();

        if (!string.Equals(nextAuthToken, _authToken, StringComparison.Ordinal))
        {
            foreach (HttpClient client in BoundClients())
            {
                client.DefaultRequestHeaders.Remove(TokenHeader);
                if (!string.IsNullOrWhiteSpace(nextAuthToken))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(TokenHeader, nextAuthToken);
                }
            }

            _authToken = nextAuthToken;
        }

        if (nextPort == _port
            && string.Equals(nextHost, _host, StringComparison.OrdinalIgnoreCase)
            && _httpClient.BaseAddress != null
            && _narrativeHttpClient.BaseAddress != null)
        {
            return true;
        }

        _port = nextPort;
        _host = nextHost;
        var baseAddress = new Uri($"http://{_host}:{_port}");
        foreach (HttpClient client in BoundClients())
        {
            client.BaseAddress = baseAddress;
        }

        return true;
    }
}
