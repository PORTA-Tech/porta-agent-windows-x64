using System.Threading.RateLimiting;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Porta.Agent.Core;
using Porta.Agent.Core.Security;
using Porta.Agent.Common.Identity;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.LocalApi;

[SupportedOSPlatform("windows")]
public static class LocalApiEndpointMapping
{
    private const string TokenHeader = "X-PORTA-TOKEN";
    private const string ApiLimiterPolicy = "porta_local_api";
    private const int MaxIdLength = 128;
    private const int MaxFocusHashLength = 128;
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex FocusHashPattern = new(@"^[a-fA-F0-9-_.:]+$", RegexOptions.Compiled);

    public static IServiceCollection AddPortaLocalApi(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(new LocalApiAckResponse(
                    Success: false,
                    Message: "rate_limited",
                    TimestampUtc: DateTimeOffset.UtcNow), cancellationToken: token).ConfigureAwait(false);
            };

            options.AddPolicy(ApiLimiterPolicy, httpContext =>
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "local",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 60,
                        Window = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    });
            });
        });

        return services;
    }

    public static WebApplication MapPortaLocalApi(this WebApplication app, LocalApiRuntimeSettings settings)
    {
        app.UseRateLimiter();

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            if (context.Connection.RemoteIpAddress is not null
                && !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new LocalApiAckResponse(
                    Success: false,
                    Message: "loopback_only",
                    TimestampUtc: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                return;
            }

            Secrets.ApiTokenReadResult authResult = Secrets.GetApiToken();
            bool localApiAuthReady = authResult.Success && authResult.TokenBytes is { Length: 32 };
            string? localApiAuthError = localApiAuthReady ? null : "TOKEN_READ_FAILED";
            string? currentAuthToken = localApiAuthReady ? Convert.ToHexString(authResult.TokenBytes!) : null;

            context.Items["local_api_auth_ready"] = localApiAuthReady;
            context.Items["local_api_auth_error"] = localApiAuthError;

            if (!localApiAuthReady)
            {
                bool isStatusEndpoint = HttpMethods.IsGet(context.Request.Method)
                    && context.Request.Path.Equals("/v1/status", StringComparison.OrdinalIgnoreCase);
                if (isStatusEndpoint)
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new LocalApiAckResponse(
                    Success: false,
                    Message: "local_api_auth_not_ready",
                    TimestampUtc: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                return;
            }

            if (!context.Request.Headers.TryGetValue(TokenHeader, out var token)
                || string.IsNullOrWhiteSpace(currentAuthToken)
                || !string.Equals(token.ToString(), currentAuthToken, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new LocalApiAckResponse(
                    Success: false,
                    Message: "missing_or_invalid_token",
                    TimestampUtc: DateTimeOffset.UtcNow)).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });

        RouteGroupBuilder api = app.MapGroup("/v1").RequireRateLimiting(ApiLimiterPolicy);

        api.MapGet("/status", (IAgentCoreController controller, HttpContext httpContext) =>
        {
            Secrets.ApiTokenReadResult tokenResult = Secrets.GetApiToken();
            bool localApiAuthReady = tokenResult.Success && tokenResult.TokenBytes is { Length: 32 };
            string? localApiAuthError = localApiAuthReady ? null : "TOKEN_READ_FAILED";

            if (controller is AgentCoreController concreteController)
            {
                concreteController.SetLocalApiAuthState(localApiAuthReady, localApiAuthError);
            }

            AgentServiceStatusSnapshot status = controller.GetStatus();
            return StatusMapper.Map(status, localApiAuthReady, localApiAuthError);
        });

        api.MapGet("/snapshot/current", (IAgentCoreController controller) =>
        {
            AgentBrainSnapshot snapshot = controller.GetCurrentSnapshot();
            AgentLatestStateDetails? latestState = controller.GetLatestStateDetails();
            return ToSnapshotResponse(snapshot, latestState);
        });

        api.MapGet("/inputs/summary", (IAgentCoreController controller, int? window) =>
        {
            AgentInputSummary summary = controller.GetInputsSummary(window ?? 60);
            return new LocalApiInputsSummaryResponse(
                summary.FromUtc,
                summary.ToUtc,
                summary.WindowSeconds,
                summary.SampleCount,
                summary.TypingBurstIntensity,
                summary.ScrollVelocityIndex,
                summary.FocusSwitchRate,
                summary.IdleFragmentationIndex,
                summary.NotificationLatencyIndex);
        });

        api.MapPost("/input/signals", (IAgentCoreController controller, LocalApiInputSignalsRequest? request) =>
        {
            if (request is null || (request.SchemaVer is not 1 and not 2))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_payload_or_schema",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            string agentInstanceId = request.AgentInstanceId?.Trim() ?? string.Empty;
            if (!IsValidIdentifier(agentInstanceId))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_agent_instance_id",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            string sessionId = request.SessionId?.Trim() ?? string.Empty;
            if (!IsValidIdentifier(sessionId))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_session_id",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            string focusAppHash = request.FocusAppHash?.Trim() ?? string.Empty;
            if (!IsValidFocusHash(focusAppHash))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_focus_app_hash",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            try
            {
                controller.IngestInputSignals(new InputSignalsPacket(
                    SchemaVer: request.SchemaVer,
                    AgentInstanceId: agentInstanceId,
                    SessionId: sessionId,
                    WindowStartUtc: request.WindowStartUtc,
                    WindowEndUtc: request.WindowEndUtc,
                    KeyboardKeydownCount: request.KeyboardKeydownCount,
                    KeyboardBackspaceCount: request.KeyboardBackspaceCount,
                    MouseWheelDelta: request.MouseWheelDelta,
                    MouseMoveDx: request.MouseMoveDx,
                    MouseMoveDy: request.MouseMoveDy,
                    FocusAppHash: string.IsNullOrWhiteSpace(focusAppHash) ? null : focusAppHash,
                    IdleSeconds: request.IdleSeconds,
                    LastInputEventUtc: request.LastInputEventUtc,
                    KeyboardIkiMeanMs: request.KeyboardIkiMeanMs,
                    KeyboardIkiStdMs: request.KeyboardIkiStdMs,
                    KeyboardIkiCv: request.KeyboardIkiCv,
                    KeyboardBurstCount: request.KeyboardBurstCount,
                    KeyboardBurstRate: request.KeyboardBurstRate,
                    KeyboardCorrectionSpikeCount: request.KeyboardCorrectionSpikeCount,
                    KeyboardCorrectionSpikeRate: request.KeyboardCorrectionSpikeRate,
                    MouseMoveSampleCount: request.MouseMoveSampleCount,
                    MousePathLengthPx: request.MousePathLengthPx,
                    MouseSpeedMeanPxPerSec: request.MouseSpeedMeanPxPerSec,
                    MouseJerkP95: request.MouseJerkP95,
                    MouseMicroCorrectionCount: request.MouseMicroCorrectionCount,
                    MouseMicroCorrectionRate: request.MouseMicroCorrectionRate,
                    MouseWheelEventCount: request.MouseWheelEventCount,
                    MouseScrollBurstCount: request.MouseScrollBurstCount,
                    MouseScrollBurstRate: request.MouseScrollBurstRate,
                    MouseScrollSpeedMean: request.MouseScrollSpeedMean,
                    FocusObservationCount: request.FocusObservationCount,
                    FocusSwitchCount: request.FocusSwitchCount,
                    FocusActiveAppCount: request.FocusActiveAppCount,
                    FocusAppEntropy: request.FocusAppEntropy,
                    IdleObservationCount: request.IdleObservationCount,
                    IdleSecondsWithinWindow: request.IdleSecondsWithinWindow,
                    MicroIdleCount: request.MicroIdleCount,
                    MicroIdleSeconds: request.MicroIdleSeconds,
                    BreakSecondsWithinWindow: request.BreakSecondsWithinWindow,
                    ShortIdleBoundaryObserved: request.ShortIdleBoundaryObserved,
                    NotificationToastsEnabled: request.NotificationToastsEnabled));

                return Results.Ok(new LocalApiAckResponse(
                    Success: true,
                    Message: "input_signals_accepted",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: ex.Message,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new LocalApiAckResponse(
                    Success: false,
                    Message: ex.Message,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapGet("/interventions/pending", (IAgentCoreController controller, string? session_id, int? limit) =>
        {
            string sessionId = session_id?.Trim() ?? string.Empty;
            if (!IsValidIdentifier(sessionId))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_session_id",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            try
            {
                IReadOnlyList<AgentPendingIntervention> pending =
                    controller.GetPendingInterventions(sessionId, limit ?? 20);
                return Results.Ok(InterventionMapper.MapPendingOrdered(pending));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new LocalApiAckResponse(
                    Success: false,
                    Message: ex.Message,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
        });

        api.MapPost("/interventions/ack", (IAgentCoreController controller, LocalApiInterventionAckRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_payload_or_schema",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            string interventionId = request.InterventionId?.Trim() ?? string.Empty;
            if (!IsValidIdentifier(interventionId))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_intervention_id",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            string sessionId = request.SessionId?.Trim() ?? string.Empty;
            if (!IsValidIdentifier(sessionId))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_session_id",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            string agentInstanceId = request.AgentInstanceId?.Trim() ?? string.Empty;
            if (!IsValidIdentifier(agentInstanceId))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_agent_instance_id",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            string result = request.Result?.Trim().ToLowerInvariant() ?? string.Empty;
            if (result is not "applied" and not "verified" and not "failed")
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_ack_result",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            try
            {
                controller.AcknowledgeIntervention(new AgentInterventionAck(
                    InterventionId: interventionId,
                    SessionId: sessionId,
                    AgentInstanceId: agentInstanceId,
                    Result: result,
                    ErrorCode: request.ErrorCode?.Trim(),
                    AppliedAtUtc: request.AppliedAtUtc ?? DateTimeOffset.UtcNow));
                return Results.Ok(new LocalApiAckResponse(
                    Success: true,
                    Message: "intervention_acknowledged",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: ex.Message,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new LocalApiAckResponse(
                    Success: false,
                    Message: ex.Message,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
        });

        api.MapGet("/events/recent", (IAgentCoreController controller, int? limit, DateTimeOffset? since) =>
        {
            // Source order is pinned in SqliteBrainStateStore query:
            // ORDER BY ts_utc DESC, id DESC
            IReadOnlyList<AgentControlEvent> events = controller.GetRecentEvents(limit ?? 50, since);
            return EventMapper.MapRecentOrdered(events);
        });

        api.MapGet("/events", (IAgentCoreController controller, HttpContext httpContext, int? limit) =>
        {
            int boundedLimit = Math.Clamp(limit ?? 200, 1, 1000);
            long sinceId = 0;
            if (httpContext.Request.Query.TryGetValue("since_id", out var sinceRaw)
                && long.TryParse(sinceRaw.ToString(), out long parsedSinceId)
                && parsedSinceId > 0)
            {
                sinceId = parsedSinceId;
            }

            IReadOnlyList<AgentControlEvent> events = controller.GetEventsSinceId(sinceId, boundedLimit);
            return events.Select(EventMapper.Map).ToList();
        });

        api.MapGet("/timeline", (IAgentCoreController controller, DateTimeOffset from, DateTimeOffset to, int? bucketSeconds) =>
        {
            int bucket = Math.Clamp(bucketSeconds ?? Time.bucketSeconds, 1, 3600);
            AgentTimelineData timeline = controller.GetTimeline(from, to, bucket);
            return new LocalApiTimelineResponse(
                timeline.FromUtc,
                timeline.ToUtc,
                timeline.BucketSeconds,
                timeline.Snapshots.Select(static snapshot => ToSnapshotResponse(snapshot)).ToList(),
                timeline.Interventions.Select(EventMapper.Map).ToList());
        });

        api.MapGet("/settings/effective", (IAgentCoreController controller) =>
        {
            AgentSettingsState state = controller.GetEffectiveSettings();
            return ToSettingsResponse(state);
        });

        api.MapGet("/data/retention", (IAgentCoreController controller) =>
        {
            AgentDataRetentionInfo info = controller.GetDataRetention();
            return new LocalApiRetentionResponse(
                info.RetentionDays,
                info.DatabaseBytes,
                info.LogBytes,
                info.ConsentBytes,
                info.TotalBytes,
                info.UpdatedAtUtc);
        });

        api.MapGet("/identity/link", (ILocalIdentityLinkStore identityLinkStore) =>
        {
            LocalIdentityLinkSnapshot snapshot = identityLinkStore.GetCurrent();
            return Results.Ok(ToIdentityLinkResponse(snapshot));
        });

        api.MapPost("/identity/link", (ILocalIdentityLinkStore identityLinkStore, LocalApiSetIdentityLinkRequest? request) =>
        {
            string? normalizedAccountId = NormalizeGuid(request?.AccountId);
            if (normalizedAccountId is null)
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_account_id",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            string? normalizedOrgId = NormalizeGuid(request?.OrgId);
            if (normalizedOrgId is null)
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_org_id",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            LocalIdentityLinkSnapshot snapshot = identityLinkStore.SetLink(normalizedAccountId, normalizedOrgId);
            return Results.Ok(ToIdentityLinkResponse(snapshot));
        });

        api.MapPost("/identity/unlink", (ILocalIdentityLinkStore identityLinkStore) =>
        {
            LocalIdentityLinkSnapshot snapshot = identityLinkStore.ClearLink();
            return Results.Ok(ToIdentityLinkResponse(snapshot));
        });

        api.MapPost("/control/pause", (IAgentCoreController controller, LocalApiPauseRequest? request) =>
        {
            controller.Pause(request?.Reason, request?.UntilUtc);
            return Results.Ok(new LocalApiAckResponse(true, "paused", DateTimeOffset.UtcNow));
        });

        api.MapPost("/control/resume", (IAgentCoreController controller) =>
        {
            controller.Resume();
            return Results.Ok(new LocalApiAckResponse(true, "resumed", DateTimeOffset.UtcNow));
        });

        api.MapPost("/consent/grant", (IAgentCoreController controller) =>
        {
            controller.GrantConsent();
            return Results.Ok(new LocalApiAckResponse(true, "consent_granted", DateTimeOffset.UtcNow));
        });

        api.MapPost("/consent/revoke", (IAgentCoreController controller) =>
        {
            controller.RevokeConsent();
            return Results.Ok(new LocalApiAckResponse(true, "consent_revoked", DateTimeOffset.UtcNow));
        });

        api.MapPost("/settings/update", (IAgentCoreController controller, LocalApiUpdateSettingsRequest? request) =>
        {
            AgentSettingsState updated = controller.UpdateSettings(new AgentSettingsUpdate(
                request?.RegulationIntensity,
                request?.PrivacyLevel,
                request?.NotificationReductionEnabled,
                request?.FocusModeEnabled,
                request?.AnimationReductionEnabled,
                request?.TemporaryAppRestrictionEnabled));
            return Results.Ok(ToSettingsResponse(updated));
        });

        api.MapPost("/baseline/recalibrate", (IAgentCoreController controller) =>
        {
            AgentRecalibrationResult result = controller.RecalibrateBaseline();
            return Results.Ok(new LocalApiRecalibrationResponse(
                result.Active,
                result.StartedUtc,
                result.UntilUtc));
        });

        api.MapPost("/data/export", (IAgentCoreController controller) =>
        {
            string bundle = controller.ExportBundle();
            return Results.Ok(new LocalApiExportResponse(true, bundle, DateTimeOffset.UtcNow));
        });

        api.MapPost("/data/delete_local", (IAgentCoreController controller) =>
        {
            controller.DeleteLocalData();
            return Results.Ok(new LocalApiAckResponse(true, "local_data_deleted", DateTimeOffset.UtcNow));
        });

        api.MapPost("/service/restart", () =>
        {
            return Results.Ok(new LocalApiAckResponse(
                Success: false,
                Message: "service_restart_not_supported_use_scm",
                TimestampUtc: DateTimeOffset.UtcNow));
        });

        api.MapPost("/testing/intervention", (IAgentCoreController controller, LocalApiTestInterventionRequest? request) =>
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Action))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "missing_action",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            try
            {
                controller.TriggerTestIntervention(request.Action);
                return Results.Ok(new LocalApiAckResponse(
                    Success: true,
                    Message: "test_intervention_triggered",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: ex.Message,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
        });

        return app;
    }

    private static bool IsValidIdentifier(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxIdLength
            && IdPattern.IsMatch(value);
    }

    private static bool IsValidFocusHash(string focusHash)
    {
        if (string.IsNullOrWhiteSpace(focusHash))
        {
            return true;
        }

        if (focusHash.Length > MaxFocusHashLength)
        {
            return false;
        }

        return FocusHashPattern.IsMatch(focusHash);
    }

    private static string? NormalizeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Guid.TryParse(value.Trim(), out Guid guid)
            ? guid.ToString("D")
            : null;
    }

    private static LocalApiCurrentSnapshotResponse ToSnapshotResponse(
        AgentBrainSnapshot snapshot,
        AgentLatestStateDetails? latestState = null)
    {
        return SnapshotMapper.Map(snapshot, latestState);
    }

    private static LocalApiSettingsResponse ToSettingsResponse(AgentSettingsState state)
    {
        return new LocalApiSettingsResponse(
            state.RegulationIntensity,
            state.PrivacyLevel,
            state.NotificationReductionEnabled,
            state.FocusModeEnabled,
            state.AnimationReductionEnabled,
            state.TemporaryAppRestrictionEnabled,
            state.BaselineRecalibrationActive,
            state.BaselineRecalibrationStartedUtc,
            state.BaselineRecalibrationUntilUtc,
            state.UpdatedAtUtc);
    }

    private static LocalApiIdentityLinkResponse ToIdentityLinkResponse(LocalIdentityLinkSnapshot snapshot)
    {
        return new LocalApiIdentityLinkResponse(
            snapshot.DeviceId,
            snapshot.AccountId,
            snapshot.OrgId,
            snapshot.LinkStatus,
            snapshot.LinkedAtUtc,
            snapshot.UpdatedAtUtc);
    }
}
