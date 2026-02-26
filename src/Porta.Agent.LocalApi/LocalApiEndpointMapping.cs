using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Porta.Agent.Core;

namespace Porta.Agent.LocalApi;

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

            if (!context.Request.Headers.TryGetValue(TokenHeader, out var token)
                || !string.Equals(token.ToString(), settings.AuthToken, StringComparison.Ordinal))
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

        api.MapGet("/status", (IAgentCoreController controller) =>
        {
            AgentServiceStatusSnapshot status = controller.GetStatus();
            return new LocalApiStatusResponse(
                status.Status,
                status.ServiceRunning,
                status.Paused,
                status.ConsentActive,
                status.BackendConnected,
                status.FocusStrength,
                status.OverloadLikelihood,
                status.LastAction,
                status.AgentVersion,
                status.SchemaVersion,
                status.PolicyVersion,
                status.LastEventTimestampUtc,
                status.LastError,
                status.LastUpdatedUtc);
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

        api.MapPost("/data/export", (IAgentCoreController controller) =>
        {
            string bundle = controller.ExportBundle();
            return Results.Ok(new LocalApiExportResponse(true, bundle, DateTimeOffset.UtcNow));
        });

        return app;
    }
}
