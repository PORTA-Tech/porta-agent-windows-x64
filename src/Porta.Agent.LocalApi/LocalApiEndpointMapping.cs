using System.Threading.RateLimiting;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Porta.Agent.Core;
using Porta.Agent.Core.Security;

namespace Porta.Agent.LocalApi;

[SupportedOSPlatform("windows")]
public static class LocalApiEndpointMapping
{
    private const string TokenHeader = "X-PORTA-TOKEN";
    private const string ApiLimiterPolicy = "porta_local_api";

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
            return new LocalApiStatusResponse(
                status.Status,
                status.ServiceRunning,
                status.Paused,
                status.PauseUntilUtc,
                status.ConsentActive,
                status.ConsentVersion,
                status.ConsentTimestampUtc,
                status.ConsentDisclosureHash,
                status.BackendConnected,
                status.FocusStrength,
                status.OverloadLikelihood,
                status.LastAction,
                status.AgentVersion,
                status.SchemaVersion,
                status.PolicyVersion,
                status.LastEventTimestampUtc,
                status.LastError,
                localApiAuthReady,
                localApiAuthError,
                status.NotificationSuppressionActive,
                status.NotificationSuppressionReady,
                status.NotificationSuppressionLastError,
                status.NotificationSuppressionBackoffUntilUtc,
                status.BrightnessActionReady,
                status.BrightnessActionActive,
                status.BrightnessSupported,
                status.BrightnessCurrentLevel,
                status.BrightnessTargetLevel,
                status.BrightnessLastError,
                status.BrightnessBackoffUntilUtc,
                status.OverloadActive,
                status.DeepWorkActive,
                status.DeepWorkStreakSeconds,
                status.VisualMode,
                status.LatestContributorsJson,
                status.LatestWhyJson,
                status.LastUpdatedUtc);
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

        api.MapGet("/events/recent", (IAgentCoreController controller, int? limit, DateTimeOffset? since) =>
        {
            IReadOnlyList<AgentControlEvent> events = controller.GetRecentEvents(limit ?? 50, since);
            return events.Select(ToEventResponse).ToList();
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
            return events.Select(ToEventResponse).ToList();
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
                timeline.Interventions.Select(ToEventResponse).ToList());
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

    private static LocalApiCurrentSnapshotResponse ToSnapshotResponse(
        AgentBrainSnapshot snapshot,
        AgentLatestStateDetails? latestState = null)
    {
        double idleFragmentationIndex = latestState?.IdleFragmentationIndex ?? snapshot.IdleFragmentationIndex;
        string stateFlagsJson = latestState?.StateFlagsJson ?? snapshot.StateFlagsJson;
        string contributorsJson = latestState?.ContributorsJson ?? snapshot.ContributorsJson;
        bool overloadActive = latestState?.OverloadActive ?? snapshot.OverloadActive;
        double sustainedAttentionSeconds = latestState?.SustainedAttentionDurationSec ?? snapshot.SustainedAttentionDurationSec;
        double entropyNorm = Clamp01((latestState?.FocusAppEntropy ?? 0d) / 2d);
        double focusChoppinessIndex = Clamp01(
            (0.50d * idleFragmentationIndex)
            + (0.35d * Clamp01(snapshot.SmoothedFocusSwitchRate))
            + (0.15d * entropyNorm));

        return new LocalApiCurrentSnapshotResponse(
            snapshot.TimestampUtc,
            snapshot.TypingBurstIntensity,
            snapshot.ScrollVelocityIndex,
            snapshot.FocusSwitchRate,
            snapshot.SmoothedTypingBurstIntensity,
            snapshot.SmoothedScrollVelocityIndex,
            snapshot.SmoothedFocusSwitchRate,
            snapshot.FocusStrength,
            snapshot.OverloadLikelihood,
            snapshot.FatigueProbability,
            snapshot.CognitiveStability,
            snapshot.DominantStateValue,
            snapshot.DominantStateName,
            snapshot.Confidence,
            snapshot.LastAction,
            snapshot.ActivationWindowTriggered,
            snapshot.CooldownTriggered,
            snapshot.ActivationWindowOpen,
            snapshot.Contributors,
            sustainedAttentionSeconds,
            idleFragmentationIndex,
            focusChoppinessIndex,
            overloadActive,
            string.IsNullOrWhiteSpace(stateFlagsJson) ? null : stateFlagsJson,
            string.IsNullOrWhiteSpace(contributorsJson) ? null : contributorsJson,
            latestState?.BaselineReady,
            latestState?.BaselineReadyPct,
            latestState?.AvgQuality2m,
            latestState?.ColdStart,
            latestState?.CurrentQualityScore,
            latestState?.PartialData,
            latestState?.MissingMajorChannels);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);

    private static LocalApiEventResponse ToEventResponse(AgentControlEvent ev)
    {
        return new LocalApiEventResponse(
            ev.EventId,
            ToEventType(ev.Action),
            ev.TimestampUtc,
            ev.IdempotencyKey,
            ev.WhyJson,
            string.IsNullOrWhiteSpace(ev.StateFlagsJson) ? null : ev.StateFlagsJson,
            ev.TimestampUtc,
            ev.Action,
            ev.TriggerState,
            ev.TriggerValue,
            ev.Confidence,
            ev.DurationSeconds,
            ev.EscalationLevel,
            ev.Reversible,
            ev.Reason,
            ev.Contributors,
            ev.InterventionId,
            ev.PolicyId,
            ev.Domain,
            ev.PlannedEndTimestampUtc,
            ev.ActualEndTimestampUtc,
            ev.PreviousSystemStateJson,
            ev.WhatChanged,
            ev.HowToUndo,
            ev.ReversalApplied,
            ev.CooldownEndTimestampUtc,
            ev.LifecycleState);
    }

    private static string ToEventType(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return "UNKNOWN";
        }

        return action.Trim().ToUpperInvariant();
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
}
