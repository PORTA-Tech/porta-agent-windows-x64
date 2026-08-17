using System.Threading.RateLimiting;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Porta.Agent.Core;
using Porta.Agent.Core.Outputs;
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

        string agentVersion = typeof(AgentCoreController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        app.MapGet("/healthz", () => Results.Json(new
        {
            alive = true,
            timestamp_utc = DateTimeOffset.UtcNow,
            process_id = Environment.ProcessId,
            agent_version = agentVersion
        }));

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
            (bool localApiAuthReady, string? localApiAuthError) = ReadLocalApiAuthState(httpContext);

            if (controller is AgentCoreController concreteController)
            {
                concreteController.SetLocalApiAuthState(localApiAuthReady, localApiAuthError);
            }

            AgentServiceStatusSnapshot status = controller.GetStatus();
            return StatusMapper.Map(status, localApiAuthReady, localApiAuthError);
        });

        api.MapGet("/snapshot/current", (IAgentCoreController controller, HttpContext httpContext) =>
        {
            (bool localApiAuthReady, string? localApiAuthError) = ReadLocalApiAuthState(httpContext);

            if (controller is AgentCoreController concreteController)
            {
                concreteController.SetLocalApiAuthState(localApiAuthReady, localApiAuthError);
            }

            if (controller is AgentCoreController projectionController)
            {
                AgentCurrentSnapshotProjection projection = projectionController.GetCurrentSnapshotProjection();
                return ToSnapshotResponse(projection.Snapshot, projection.LatestState, projection.Status);
            }

            AgentBrainSnapshot snapshot = controller.GetCurrentSnapshot();
            AgentLatestStateDetails? latestState = controller.GetLatestStateDetails();
            AgentServiceStatusSnapshot status = controller.GetStatus();
            return ToSnapshotResponse(snapshot, latestState, status);
        });

        api.MapGet("/session/live", (IAgentCoreController controller) =>
        {
            AgentLiveSessionState state = controller.GetLiveSessionState();
            return LiveSessionMapper.Map(state);
        });

        api.MapGet("/session/ari-context", (IAriContextProvider ariContext) =>
        {
            return ariContext.GetContext();
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

        // Phase 1b — Step 3b: sampled ground-truth prompts.
        api.MapGet("/labels/prompt-due", (IAgentCoreController controller, bool? meeting) =>
        {
            AgentLabelPromptDecision decision = controller.GetLabelPromptDecision(meeting ?? false);
            return new LocalApiLabelPromptDueResponse(
                ShouldPrompt: decision.ShouldPrompt,
                LabelTarget: decision.LabelTarget,
                PromptKind: decision.PromptKind,
                ReasonCode: decision.ReasonCode,
                TimestampUtc: DateTimeOffset.UtcNow);
        });

        api.MapPost("/labels/focus", (IAgentCoreController controller, LocalApiFocusLabelRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_payload_or_schema",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            try
            {
                bool recorded = controller.RecordFocusLabel(new AgentFocusLabelRequest(
                    LabelTarget: request.LabelTarget?.Trim() ?? string.Empty,
                    PromptKind: request.PromptKind?.Trim() ?? string.Empty,
                    Answer: request.Answer?.Trim() ?? string.Empty,
                    ResponseLatencyMs: request.ResponseLatencyMs));

                if (!recorded)
                {
                    return Results.BadRequest(new LocalApiAckResponse(
                        Success: false,
                        Message: "unrecognized_label",
                        TimestampUtc: DateTimeOffset.UtcNow));
                }

                return Results.Ok(new LocalApiAckResponse(
                    Success: true,
                    Message: "label_recorded",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }
            catch (Exception)
            {
                return Results.Json(
                    new LocalApiAckResponse(
                        Success: false,
                        Message: "label_record_failed",
                        TimestampUtc: DateTimeOffset.UtcNow),
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // Phase 1b — Step 4: local readout of accumulated ground-truth labels (counts only).
        api.MapGet("/labels/diagnostics", (IAgentCoreController controller) =>
        {
            AgentLabelDiagnostics d = controller.GetLabelDiagnostics();
            return new LocalApiLabelDiagnosticsResponse(
                TotalLabels: d.TotalLabels,
                LabelsToday: d.LabelsLast24h,
                LabelsThisWeek: d.LabelsLast7d,
                ByTarget: d.Last24hByTarget
                    .Select(t => new LocalApiLabelTargetCount(t.Target, t.Count, t.NetValue))
                    .ToList(),
                TimestampUtc: DateTimeOffset.UtcNow);
        });

        // Phase 2c — reminders. Polling /reminders/next IS the reminder clock: the store
        // lazily transitions pending→fired on each read, so no background timer exists.
        api.MapGet("/reminders/next", (IAgentCoreController controller) =>
        {
            AgentReminder? next = controller.GetNextReminder();
            return new LocalApiNextReminderResponse(
                Next: ToApiReminder(next),
                TimestampUtc: DateTimeOffset.UtcNow);
        });

        api.MapPost("/reminders", (IAgentCoreController controller, LocalApiReminderCreateRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiReminderCreateResponse(
                    Success: false, Reason: "invalid_payload_or_schema", Reminder: null,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            AgentReminder? created = controller.CreateReminder(request.Title ?? string.Empty, request.DueAtUtc);
            return created is null
                ? Results.Json(new LocalApiReminderCreateResponse(
                    Success: false, Reason: "cap_reached", Reminder: null,
                    TimestampUtc: DateTimeOffset.UtcNow))
                : Results.Json(new LocalApiReminderCreateResponse(
                    Success: true, Reason: null, Reminder: ToApiReminder(created),
                    TimestampUtc: DateTimeOffset.UtcNow));
        });

        api.MapPost("/reminders/ack", (IAgentCoreController controller, LocalApiReminderAckRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false, Message: "invalid_payload_or_schema", TimestampUtc: DateTimeOffset.UtcNow));
            }

            bool acknowledged = controller.AcknowledgeReminder(request.Id, request.Action ?? string.Empty);
            return acknowledged
                ? Results.Json(new LocalApiAckResponse(
                    Success: true, Message: "ok", TimestampUtc: DateTimeOffset.UtcNow))
                : Results.BadRequest(new LocalApiAckResponse(
                    Success: false, Message: "unknown_reminder_or_action", TimestampUtc: DateTimeOffset.UtcNow));
        });

        // Workstream 4 — timed tasks (plan vs. reality). Same auth/contract patterns as
        // reminders; attribution happens in the bucket pipeline, these endpoints only
        // manage lifecycle and read the measured numbers.
        // goalDaysBack widens the window backwards for step 8's "yesterday" block: the morning card
        // fires after the 05:00 boundary, so yesterday's closed tasks are already outside today's
        // window at exactly the moment the card wants to show them. Absent or 0 = today only, so
        // every existing caller behaves exactly as before.
        api.MapGet("/tasks/today", (IAgentCoreController controller, int? goalDaysBack) =>
        {
            IReadOnlyList<AgentTask> tasks = goalDaysBack is > 0
                ? controller.GetRecentTasks(goalDaysBack.Value)
                : controller.GetTodayTasks();
            return new LocalApiTasksTodayResponse(
                Tasks: tasks.Select(ToApiTask).ToList()!,
                TimestampUtc: DateTimeOffset.UtcNow);
        });

        api.MapPost("/tasks", (IAgentCoreController controller, LocalApiTaskCreateRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiTaskCreateResponse(
                    Success: false, Reason: "invalid_payload_or_schema", Task: null,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            AgentTask? created = controller.CreateTask(
                request.Title ?? string.Empty, request.PlannedSeconds, request.ProjectId);
            return created is null
                ? Results.Json(new LocalApiTaskCreateResponse(
                    Success: false, Reason: "cap_reached_or_invalid", Task: null,
                    TimestampUtc: DateTimeOffset.UtcNow))
                : Results.Json(new LocalApiTaskCreateResponse(
                    Success: true, Reason: null, Task: ToApiTask(created),
                    TimestampUtc: DateTimeOffset.UtcNow));
        });

        api.MapPost("/tasks/act", (IAgentCoreController controller, LocalApiTaskActRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiTaskActResponse(
                    Success: false, Task: null, TimestampUtc: DateTimeOffset.UtcNow));
            }

            AgentTask? updated = controller.ActOnTask(request.Id, request.Action ?? string.Empty);
            return updated is null
                ? Results.BadRequest(new LocalApiTaskActResponse(
                    Success: false, Task: null, TimestampUtc: DateTimeOffset.UtcNow))
                : Results.Json(new LocalApiTaskActResponse(
                    Success: true, Task: ToApiTask(updated), TimestampUtc: DateTimeOffset.UtcNow));
        });

        // Step 9a — projects. Local only: nothing here feeds any payload that leaves the machine.
        // Ari never guesses a project, so there is no inference endpoint and never will be — every
        // value that arrives here came from the user tapping something.
        api.MapGet("/projects", (IAgentCoreController controller, bool? includeArchived) =>
            new LocalApiProjectsResponse(
                Projects: controller.GetProjects(includeArchived ?? false).Select(ToApiProject).ToList()!,
                TimestampUtc: DateTimeOffset.UtcNow));

        api.MapPost("/projects", (IAgentCoreController controller, LocalApiProjectCreateRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiProjectResponse(
                    Success: false, Reason: "invalid_payload_or_schema", Project: null,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            // Create-or-get: a name that already exists returns that project rather than failing.
            AgentProject? project = controller.CreateProject(request.Name ?? string.Empty);
            return project is null
                ? Results.Json(new LocalApiProjectResponse(
                    Success: false, Reason: "cap_reached_or_invalid", Project: null,
                    TimestampUtc: DateTimeOffset.UtcNow))
                : Results.Json(new LocalApiProjectResponse(
                    Success: true, Reason: null, Project: ToApiProject(project),
                    TimestampUtc: DateTimeOffset.UtcNow));
        });

        // Step 10 phase A/B — the report's own surface. LOCAL ONLY, and there is deliberately no
        // send endpoint here: sending is phase C, and until it exists nothing built on this can
        // put anything in front of a client.
        api.MapPost("/reports/label", (IAgentCoreController controller, LocalApiTaskClientLabelRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiTaskActResponse(
                    Success: false, Task: null, TimestampUtc: DateTimeOffset.UtcNow));
            }

            AgentTask? updated = controller.SetTaskClientLabel(request.TaskId, request.ClientLabel ?? string.Empty);
            return updated is null
                ? Results.Json(new LocalApiTaskActResponse(
                    Success: false, Task: null, TimestampUtc: DateTimeOffset.UtcNow))
                : Results.Json(new LocalApiTaskActResponse(
                    Success: true, Task: ToApiTask(updated), TimestampUtc: DateTimeOffset.UtcNow));
        });

        api.MapPost("/projects/client", (IAgentCoreController controller, LocalApiProjectClientRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiProjectResponse(
                    Success: false, Reason: "invalid_payload_or_schema", Project: null,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            // Each field applies only when present, so setting a rate cannot clear a client name.
            AgentProject? updated = null;
            if (request.ClientName is not null)
            {
                updated = controller.SetProjectClientName(request.Id, request.ClientName);
            }

            if (request.UseClientLabels is { } style)
            {
                updated = controller.SetProjectLabelStyle(request.Id, style);
            }

            if (request.RateMinor is { } rate)
            {
                updated = controller.SetProjectRate(request.Id, rate, request.RateCurrency ?? string.Empty);
                if (updated is null)
                {
                    // A negative rate is refused rather than stored. Say so, rather than reporting
                    // a success that changed nothing.
                    return Results.Json(new LocalApiProjectResponse(
                        Success: false, Reason: "invalid_rate", Project: null,
                        TimestampUtc: DateTimeOffset.UtcNow));
                }
            }

            return updated is null
                ? Results.Json(new LocalApiProjectResponse(
                    Success: false, Reason: "not_found_or_nothing_to_do", Project: null,
                    TimestampUtc: DateTimeOffset.UtcNow))
                : Results.Json(new LocalApiProjectResponse(
                    Success: true, Reason: null, Project: ToApiProject(updated),
                    TimestampUtc: DateTimeOffset.UtcNow));
        });

        api.MapGet("/reports/draft", (IAgentCoreController controller, long? projectId, string? from, string? to) =>
        {
            if (projectId is null or 0 || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                return Results.BadRequest(new LocalApiReportDraftResponse(
                    Success: false, Reason: "project_and_range_required", Draft: null,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            AgentReportDraft draft = controller.GetReportDraft(projectId.Value, from!, to!);
            return Results.Json(new LocalApiReportDraftResponse(
                Success: true, Reason: null, Draft: ToApiReportDraft(draft),
                TimestampUtc: DateTimeOffset.UtcNow));
        });

        // ── Step 10 phase C/D — the address book, the confirm step, and sending ────────────────
        //
        // The address book is LOCAL. These routes read and write local SQLite and nothing else;
        // there is no route that uploads the book (§12.4).

        api.MapGet("/reports/recipients", (IAgentCoreController controller) =>
            Results.Json(new LocalApiReportRecipientsResponse(
                controller.GetRecipients()
                    .Select(r => new LocalApiReportRecipient(r.Id, r.Name, r.Email, r.LastUsedUtc))
                    .ToList())));

        api.MapPost("/reports/recipients", (IAgentCoreController controller, LocalApiReportRecipientRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1 || string.IsNullOrWhiteSpace(request.Email))
            {
                return Results.BadRequest(new { success = false, reason = "invalid_payload_or_schema" });
            }

            AgentReportRecipient? saved = controller.UpsertRecipient(request.Name ?? string.Empty, request.Email!);
            return saved is null
                ? Results.BadRequest(new { success = false, reason = "invalid_email" })
                : Results.Json(new { success = true, recipient = new LocalApiReportRecipient(
                    saved.Id, saved.Name, saved.Email, saved.LastUsedUtc) });
        });

        api.MapDelete("/reports/recipients/{id:long}", (IAgentCoreController controller, long id) =>
            Results.Json(new { success = controller.DeleteRecipient(id) }));

        // What the confirm screen needs before it can decide what to show. The From name and
        // Reply-to are the SERVER's to resolve — this only reports whether identity is linked and
        // whether the preview is mandatory for this recipient.
        api.MapGet("/reports/send-context", async (
            [FromServices] IReportGateway gateway, [FromServices] IAgentCoreController controller, string? recipientEmail, CancellationToken token) =>
        {
            ReportGatewayIdentity identity = await gateway.ReadIdentityAsync(token).ConfigureAwait(false);

            // Mandatory for a first send to a new client, optional afterwards. A failed attempt does
            // not discharge it: HasSentToRecipientBefore counts only send_state = 'sent'.
            bool previewRequired =
                string.IsNullOrWhiteSpace(recipientEmail)
                || !controller.HasSentToRecipientBefore(recipientEmail!);

            return Results.Json(new LocalApiSendContext(
                FromName: identity.FromName,
                FromAddress: "reports@useporta.com",
                ReplyTo: identity.ReplyTo,
                IdentityResolved: identity.Resolved,
                PreviewRequired: previewRequired));
        });

        api.MapGet("/reports/sent", (IAgentCoreController controller) =>
            Results.Json(new LocalApiSentReportsResponse(
                controller.GetSentReports().Select(ToApiSentReport).ToList())));

        api.MapPost("/reports/preview", async (
            IAgentCoreController controller,
            [FromServices] IReportGateway gateway,
            LocalApiReportSendRequest? request,
            CancellationToken token) =>
        {
            LocalApiReportPreviewResponse bad(string reason, IReadOnlyList<string>? refusals = null)
                => new(Success: false, HtmlPath: null, Reason: reason, Refusals: refusals);

            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(bad("invalid_payload_or_schema"));
            }

            ReportSendAssembly assembly = AssembleReport(controller, request);
            if (!assembly.Ok)
            {
                // Carry the refusal list, not just the word "refused". Without it the UI cannot tell
                // an unnamed project from an empty From name, and the user is told to fix "something".
                return Results.Json(bad(assembly.Reason!, assembly.Refusals));
            }

            ReportGatewayPreview preview = await gateway
                .PreviewAsync(assembly.Json!, token)
                .ConfigureAwait(false);

            if (!preview.Success || string.IsNullOrWhiteSpace(preview.Html))
            {
                return Results.Json(bad(preview.Reason ?? "preview_failed"));
            }

            // Written to a temp file and opened in the browser rather than rendered in-app. It
            // carries a client's name and hours, so it is deleted after being opened — see
            // ReportPreviewFile.
            string path = ReportPreviewFile.Write(preview.Html!);
            return Results.Json(new LocalApiReportPreviewResponse(true, path, null));
        });

        // THE SEND. Three bounds from §12.4 are enforced here and on the server, and the order of
        // the statements below IS the third one: the local record is written before anything is
        // attempted, so a crash mid-send leaves evidence rather than a silence.
        api.MapPost("/reports/send", async (
            IAgentCoreController controller,
            [FromServices] IReportGateway gateway,
            LocalApiReportSendRequest? request,
            CancellationToken token) =>
        {
            LocalApiReportSendResponse bad(string reason, IReadOnlyList<string>? refusals = null)
                => new(Success: false, ReportId: string.Empty, Refusals: refusals ?? [], Reason: reason, SentAtUtc: null);

            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(bad("invalid_payload_or_schema"));
            }

            ReportSendAssembly assembly = AssembleReport(controller, request);
            if (!assembly.Ok)
            {
                return Results.Json(bad(assembly.Reason!, assembly.Refusals));
            }

            // Mandatory preview for a first send to a new client. Checked server-side of the UI, so
            // a caller cannot skip it by talking to the local API directly.
            if (!request.Previewed && !controller.HasSentToRecipientBefore(assembly.RecipientEmail!))
            {
                return Results.Json(bad("preview_required"));
            }

            // Bound 3 — RECORD FIRST. If this returns false the id already exists, which means this
            // is a retry of something already recorded and must not be sent again.
            if (!controller.RecordSentReport(assembly.Record!))
            {
                return Results.Json(bad("already_recorded"));
            }

            ReportGatewayOutcome outcome = await gateway.SendAsync(assembly.Json!, token).ConfigureAwait(false);

            if (outcome.Unknown)
            {
                // We do not know, so we do not claim. The record stays 'pending' and the user is
                // told the truth — a "failed" here would invite a retry, and the retry is what puts
                // a second copy in a client's inbox.
                return Results.Json(new LocalApiReportSendResponse(
                    Success: false, ReportId: assembly.ReportId!, Refusals: [],
                    Reason: "unknown", SentAtUtc: null));
            }

            controller.MarkSentReportOutcome(
                assembly.ReportId!, outcome.Sent, outcome.SentAtUtc ?? string.Empty, outcome.Reason ?? string.Empty);

            if (!outcome.Sent)
            {
                return Results.Json(bad(outcome.Reason ?? "send_failed"));
            }

            controller.MarkRecipientUsed(assembly.RecipientEmail!);

            return Results.Json(new LocalApiReportSendResponse(
                Success: true, ReportId: assembly.ReportId!, Refusals: [],
                Reason: null, SentAtUtc: outcome.SentAtUtc));
        });

        // DELETE ON BOTH SIDES. The server half runs FIRST and the local half only if it succeeded,
        // so a failure leaves the report listed as present. Never report success on a half-delete —
        // that is the primitive_buckets failure and it must not happen twice in one product.
        api.MapDelete("/reports/{reportId}", async (
            IAgentCoreController controller,
            [FromServices] IReportGateway gateway,
            string reportId,
            CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(reportId))
            {
                return Results.BadRequest(new { success = false, reason = "report_id_required" });
            }

            controller.MarkSentReportDeletePending(reportId);

            bool serverDeleted = await gateway.DeleteAsync(reportId, token).ConfigureAwait(false);
            if (!serverDeleted)
            {
                return Results.Json(new
                {
                    success = false,
                    reason = "server_delete_failed",
                    note = "The server copy is still there, so the report is still listed."
                });
            }

            controller.DeleteSentReportLocally(reportId);
            return Results.Json(new { success = true });
        });

        api.MapPost("/projects/update", (IAgentCoreController controller, LocalApiProjectUpdateRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiProjectResponse(
                    Success: false, Reason: "invalid_payload_or_schema", Project: null,
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            AgentProject? updated = null;
            if (request.Archived is { } archived)
            {
                updated = controller.SetProjectArchived(request.Id, archived);
            }

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                // A rename that collides with another project returns null, and the whole call
                // reports failure — a silent no-op would read as a rename that worked.
                updated = controller.RenameProject(request.Id, request.Name!);
                if (updated is null)
                {
                    return Results.Json(new LocalApiProjectResponse(
                        Success: false, Reason: "name_taken", Project: null,
                        TimestampUtc: DateTimeOffset.UtcNow));
                }
            }

            return updated is null
                ? Results.BadRequest(new LocalApiProjectResponse(
                    Success: false, Reason: "unknown_project", Project: null,
                    TimestampUtc: DateTimeOffset.UtcNow))
                : Results.Json(new LocalApiProjectResponse(
                    Success: true, Reason: null, Project: ToApiProject(updated),
                    TimestampUtc: DateTimeOffset.UtcNow));
        });

        api.MapPost("/tasks/project", (IAgentCoreController controller, LocalApiTaskProjectRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiTaskActResponse(
                    Success: false, Task: null, TimestampUtc: DateTimeOffset.UtcNow));
            }

            AgentTask? updated = controller.SetTaskProject(request.TaskId, request.ProjectId);
            return updated is null
                ? Results.BadRequest(new LocalApiTaskActResponse(
                    Success: false, Task: null, TimestampUtc: DateTimeOffset.UtcNow))
                : Results.Json(new LocalApiTaskActResponse(
                    Success: true, Task: ToApiTask(updated), TimestampUtc: DateTimeOffset.UtcNow));
        });

        // Workstream D — conversational check-in goals (goal-day resolved at the 5:00 boundary
        // in the controller). All four return the current goal row.
        api.MapGet("/goals/today", (IAgentCoreController controller) =>
            new LocalApiDailyGoalResponse(
                Success: true,
                Goal: ToApiGoal(controller.GetTodayGoal()),
                TimestampUtc: DateTimeOffset.UtcNow,
                CarriedPlanTomorrow: controller.GetCarriedOverPlan()));

        api.MapPost("/goals/morning", (IAgentCoreController controller, LocalApiGoalMorningRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiDailyGoalResponse(false, null, DateTimeOffset.UtcNow));
            }

            AgentDailyGoal? goal = controller.SetMorningGoal(
                request.GoalText ?? string.Empty, request.TrackingPref ?? string.Empty, request.TaskCount);
            return Results.Json(new LocalApiDailyGoalResponse(goal is not null, ToApiGoal(goal), DateTimeOffset.UtcNow));
        });

        api.MapPost("/goals/midday", (IAgentCoreController controller, LocalApiGoalMiddayRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiDailyGoalResponse(false, null, DateTimeOffset.UtcNow));
            }

            AgentDailyGoal? goal = controller.RecordMiddayAnswer(request.Answer ?? string.Empty);
            return Results.Json(new LocalApiDailyGoalResponse(goal is not null, ToApiGoal(goal), DateTimeOffset.UtcNow));
        });

        // Part 5 — one narrative line per high-value moment. The gate has already decided to
        // speak before this is called; a null line means "use the template". Fail-soft: this
        // endpoint never blocks a card beyond the caller's own timeout budget.
        api.MapPost("/narrative", async (INarrativeGateway narrative, LocalApiNarrativeRequest? request, CancellationToken token) =>
        {
            if (request is null || request.SchemaVer != 1 || string.IsNullOrWhiteSpace(request.Kind))
            {
                return Results.BadRequest(new LocalApiNarrativeResponse(
                    false, null, DateTimeOffset.UtcNow, "invalid_payload_or_schema"));
            }

            NarrativeGatewayResult result = await narrative
                .GetNarrativeLineWithReasonAsync(request.Kind.Trim(), request.UserText, token)
                .ConfigureAwait(false);
            return Results.Json(new LocalApiNarrativeResponse(
                true, result.Line, DateTimeOffset.UtcNow, result.Reason));
        });

        api.MapPost("/goals/eod", (IAgentCoreController controller, LocalApiGoalEndOfDayRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
            {
                return Results.BadRequest(new LocalApiDailyGoalResponse(false, null, DateTimeOffset.UtcNow));
            }

            AgentDailyGoal? goal = controller.RecordEndOfDay(request.PlanTomorrow ?? string.Empty);
            return Results.Json(new LocalApiDailyGoalResponse(goal is not null, ToApiGoal(goal), DateTimeOffset.UtcNow));
        });

        // Phase 1c — signup nudge: is this UNLINKED device due to be nudged to sign in right now?
        // Link status comes from the local identity store; the governor + caps live in the core.
        api.MapGet("/nudges/signup-due", (IAgentCoreController controller, ILocalIdentityLinkStore identityLinkStore) =>
        {
            LocalIdentityLinkSnapshot link = identityLinkStore.GetCurrent();
            bool isLinked = !string.IsNullOrWhiteSpace(link.AccountId);
            AgentSignupNudgeDecision decision = controller.GetSignupNudgeDecision(isLinked);
            return new LocalApiSignupNudgeDueResponse(
                ShouldNudge: decision.ShouldNudge,
                ReasonCode: decision.ReasonCode,
                TimestampUtc: DateTimeOffset.UtcNow);
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
                    NotificationToastsEnabled: request.NotificationToastsEnabled,
                    HeartbeatSequence: request.HeartbeatSequence,
                    HeartbeatUpdatedAtUtc: request.HeartbeatUpdatedAtUtc,
                    HeartbeatCaptureContext: request.HeartbeatCaptureContext,
                    FocusAppCategory: request.FocusAppCategory,
                    FocusAppIsMeeting: request.FocusAppIsMeeting,
                    FocusAppLabel: request.FocusAppLabel));

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
                return Results.Json(
                    new LocalApiAckResponse(
                        Success: false,
                        Message: "input_signals_ingest_failed",
                        TimestampUtc: DateTimeOffset.UtcNow),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapPost("/session/live", (IAgentCoreController controller, LocalApiLiveSessionStateRequest? request) =>
        {
            if (request is null || request.SchemaVer != 1)
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
                controller.PublishLiveSessionState(new AgentLiveSessionSignalPacket(
                    AgentInstanceId: agentInstanceId,
                    SessionId: sessionId,
                    UpdatedAtUtc: request.UpdatedAtUtc,
                    SessionStartedAtUtc: request.SessionStartedAtUtc,
                    LastInputEventUtc: request.LastInputEventUtc,
                    KeyboardActive: request.KeyboardActive,
                    MouseActive: request.MouseActive,
                    IdleSeconds: request.IdleSeconds,
                    FocusAppHash: string.IsNullOrWhiteSpace(focusAppHash) ? null : focusAppHash,
                    FocusStartedAtUtc: request.FocusStartedAtUtc,
                    HeartbeatSequence: request.HeartbeatSequence,
                    HeartbeatUpdatedAtUtc: request.HeartbeatUpdatedAtUtc,
                    HeartbeatCaptureContext: request.HeartbeatCaptureContext));

                return Results.Ok(new LocalApiAckResponse(
                    Success: true,
                    Message: "live_session_state_accepted",
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
                return Results.Json(
                    new LocalApiAckResponse(
                        Success: false,
                        Message: "live_session_publish_failed",
                        TimestampUtc: DateTimeOffset.UtcNow),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
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
            if (result is not "applied"
                and not "verified"
                and not "failed"
                and not "overlay_started"
                and not "accepted"
                and not "skipped"
                and not "dismissed"
                and not "snoozed"
                and not "suppressed_by_preference"
                and not "overlay_recovered"
                and not "overlay_completed"
                and not "overlay_aborted")
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
                    AppliedAtUtc: request.AppliedAtUtc ?? DateTimeOffset.UtcNow,
                    InteractionKind: request.InteractionKind?.Trim(),
                    DeliverySurface: request.DeliverySurface?.Trim(),
                    BreakKind: request.BreakKind?.Trim()));
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
            Secrets.TryDeleteDeviceCredential();
            return Results.Ok(ToIdentityLinkResponse(snapshot));
        });

        // The Control Center receives the device credential from the control plane -- it is the
        // only process holding a user session -- but the service owns the secrets directory.
        // This is the hand-off. Loopback plus the local API token guard it like every other
        // endpoint here; the secret never touches a user-writable location.
        api.MapPost("/identity/device-credential", (
            ILocalIdentityLinkStore identityLinkStore,
            LocalApiSetDeviceCredentialRequest? request) =>
        {
            string? deviceId = NormalizeGuid(request?.DeviceId);
            if (deviceId is null)
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_device_id",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            if (string.IsNullOrWhiteSpace(request?.DeviceSecret))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "invalid_device_secret",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            // A credential is only meaningful for the device this machine actually is. Accepting
            // one for a different deviceId would let a signed-in user park another machine's
            // credential here, and every fleet call would then claim to be that machine.
            LocalIdentityLinkSnapshot snapshot = identityLinkStore.GetCurrent();
            if (!string.Equals(snapshot.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new LocalApiAckResponse(
                    Success: false,
                    Message: "device_id_mismatch",
                    TimestampUtc: DateTimeOffset.UtcNow));
            }

            bool stored = Secrets.TryWriteDeviceCredential(deviceId, request.DeviceSecret);
            return stored
                ? Results.Ok(new LocalApiAckResponse(true, "device_credential_stored", DateTimeOffset.UtcNow))
                : Results.Json(
                    new LocalApiAckResponse(false, "device_credential_write_failed", DateTimeOffset.UtcNow),
                    statusCode: 500);
        });

        api.MapGet("/identity/device-credential/status", () =>
        {
            Secrets.DeviceCredentialReadResult credential = Secrets.ReadDeviceCredential();
            return Results.Ok(new LocalApiDeviceCredentialStatusResponse(
                Present: credential.Success,
                DeviceId: credential.DeviceId,
                TimestampUtc: DateTimeOffset.UtcNow));
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
        AgentLatestStateDetails? latestState = null,
        AgentServiceStatusSnapshot? status = null)
    {
        return SnapshotMapper.Map(snapshot, latestState, status);
    }

    private static (bool LocalApiAuthReady, string? LocalApiAuthError) ReadLocalApiAuthState(HttpContext httpContext)
    {
        bool localApiAuthReady = httpContext.Items.TryGetValue("local_api_auth_ready", out object? readyValue)
            && readyValue is bool ready
            && ready;
        string? localApiAuthError = httpContext.Items.TryGetValue("local_api_auth_error", out object? errorValue)
            ? errorValue as string
            : null;
        return (localApiAuthReady, localApiAuthError);
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

    // Phase 2c — reminders.
    private static LocalApiReminder? ToApiReminder(AgentReminder? reminder)
    {
        return reminder is null
            ? null
            : new LocalApiReminder(
                Id: reminder.Id,
                Title: reminder.Title,
                DueAtUtc: reminder.DueAtUtc,
                State: reminder.State,
                SnoozeCount: reminder.SnoozeCount);
    }

    // Workstream 4 — timed tasks.
    private static LocalApiTask? ToApiTask(AgentTask? task)
    {
        return task is null
            ? null
            : new LocalApiTask(
                Id: task.Id,
                Title: task.Title,
                PlannedSeconds: task.PlannedSeconds,
                State: task.State,
                ActiveSeconds: task.ActiveSeconds,
                FocusedSeconds: task.FocusedSeconds,
                OfftaskSeconds: task.OfftaskSeconds,
                ExtendedSeconds: task.ExtendedSeconds,
                CreatedAtUtc: task.CreatedAtUtc,
                StartedAtUtc: task.StartedAtUtc,
                EndedAtUtc: task.EndedAtUtc,
                DeferredUntilDay: task.DeferredUntilDay,
                ProjectId: task.ProjectId,
                ProjectName: task.ProjectName);
    }

    private static LocalApiProject ToApiProject(AgentProject project)
        => new(
            Id: project.Id,
            Name: project.Name,
            Archived: project.Archived,
            CreatedAtUtc: project.CreatedAtUtc,
            ClientName: project.ClientName,
            UseClientLabels: project.UseClientLabels,
            RateMinor: project.RateMinor,
            RateCurrency: project.RateCurrency);

    private static LocalApiReportDraft ToApiReportDraft(AgentReportDraft draft)
        => new(
            ProjectId: draft.ProjectId,
            ProjectName: draft.ProjectName,
            ClientName: draft.ClientName,
            RangeStartLocal: draft.RangeStartLocal,
            RangeEndLocal: draft.RangeEndLocal,
            TotalHours: draft.TotalHours,
            DaysWorked: draft.DaysWorked,
            LinesFinished: draft.LinesFinished,
            Lines: draft.Lines
                .Select(l => new LocalApiReportLine(
                    l.TaskId, l.Title, l.ClientLabel, l.Finished, l.Hours,
                    l.UnattributedHours, l.UnattributedMeasurable))
                .ToList(),
            Days: draft.Days
                .Select(d => new LocalApiReportDay(d.DateLocal, d.Hours))
                .ToList());

    private static LocalApiSentReport ToApiSentReport(AgentSentReport r)
        => new(
            r.ReportId, r.ClientName, r.RangeStartLocal, r.RangeEndLocal,
            r.RecipientName, r.RecipientEmail, r.FromName, r.ReplyTo, r.TotalHours,
            r.RequestedAtUtc.ToString("O"), r.SentAtUtc, r.SendState, r.SendError, r.ServerState,
            r.Lines.Select(l => new LocalApiSentReportLine(l.LineId, l.ClientLabel, l.Finished, l.Hours)).ToList());

    /// <summary>
    /// Build the approved payload (§12.2) and run every rule on the way. Both the preview route and
    /// the send route call this, so a preview can never show something the send would refuse.
    ///
    /// The excluded lines are dropped here, not flagged: an excluded line is absent from the payload
    /// (§12.3), and its hours leave the total, which is why the total is recomputed from what
    /// remains rather than carried over from the draft.
    /// </summary>
    private static ReportSendAssembly AssembleReport(
        IAgentCoreController controller,
        LocalApiReportSendRequest request)
    {
        static ReportSendAssembly Fail(string reason, IReadOnlyList<string>? refusals = null)
            => new(false, reason, refusals, null, null, null, null);

        if (request.ProjectId <= 0 || string.IsNullOrWhiteSpace(request.From) || string.IsNullOrWhiteSpace(request.To))
        {
            return Fail("project_and_range_required");
        }

        string recipientEmail = (request.RecipientEmail ?? string.Empty).Trim();
        string fromName = (request.FromName ?? string.Empty).Trim();
        string replyTo = (request.ReplyTo ?? string.Empty).Trim();

        AgentReportDraft draft = controller.GetReportDraft(request.ProjectId, request.From, request.To);
        IReadOnlyCollection<long> excluded = request.ExcludedTaskIds ?? [];

        // THE GATE. Document and envelope both, and every reason at once so the screen can list them
        // rather than making the user fix one and discover the next.
        ReportSendVerdict verdict = ReportSendPreflight.Evaluate(
            draft, excluded, fromName, replyTo, recipientEmail);

        if (!verdict.CanSend)
        {
            return Fail("refused", verdict.Refusals.Select(r => r.ToString()).ToList());
        }

        List<AgentReportLine> included = draft.Lines
            .Where(l => !excluded.Contains(l.TaskId))
            .ToList();

        // The total is the sum of the ROUNDED lines, so a client adding them by hand arrives at the
        // figure at the top. Never a separately rounded grand total.
        double total = Math.Round(included.Sum(l => l.Hours), 1, MidpointRounding.AwayFromZero);

        string reportId = Guid.NewGuid().ToString();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var lines = included
            .Select((l, i) => new { Line = l, Id = $"l{i + 1}" })
            .ToList();

        var payload = new
        {
            reportId,
            // LEFT EMPTY ON PURPOSE, AND FILLED BY ReportGateway.StampIdentity BEFORE THE POST.
            // Identity lives in the link store on the service side, not here. These three were
            // empty and stayed empty all the way to the server, which reads accountId from the body
            // and refuses without it — so preview and send both answered 400 and no report could
            // leave. machineId is opaque (MachineFingerprint), never the Windows computer name.
            accountId = string.Empty,
            deviceId = string.Empty,
            machineId = string.Empty,
            requestedAtUtc = now.ToString("O"),
            timezoneOffsetMinutes = (int)DateTimeOffset.Now.Offset.TotalMinutes,
            // The CLIENT-FACING name (§12.7). The local one never leaves the machine.
            projectName = draft.ClientName,
            rangeStartLocal = draft.RangeStartLocal,
            rangeEndLocal = draft.RangeEndLocal,
            recipientEmail,
            recipientName = (request.RecipientName ?? string.Empty).Trim(),
            totalHours = total,
            daysWorked = draft.DaysWorked,
            tasksCompleted = included.Count(l => l.Finished),
            tasksTotal = included.Count,
            lines = lines.Select(x => new
            {
                lineId = x.Id,
                clientLabel = x.Line.ClientLabel,
                finished = x.Line.Finished,
                hours = x.Line.Hours
            }).ToList(),
            // Days with 0.0 are KEPT — §4 requires gaps to be drawable.
            daily = draft.Days.Select(d => new { dateLocal = d.DateLocal, hours = d.Hours }).ToList()
        };

        var record = new AgentSentReport(
            ReportId: reportId,
            ProjectId: request.ProjectId,
            ProjectName: draft.ProjectName,
            ClientName: draft.ClientName,
            RangeStartLocal: draft.RangeStartLocal,
            RangeEndLocal: draft.RangeEndLocal,
            RecipientName: (request.RecipientName ?? string.Empty).Trim(),
            RecipientEmail: recipientEmail,
            FromName: fromName,
            ReplyTo: replyTo,
            TotalHours: total,
            DaysWorked: draft.DaysWorked,
            LinesFinished: included.Count(l => l.Finished),
            LinesTotal: included.Count,
            RequestedAtUtc: now,
            SentAtUtc: string.Empty,
            SendState: "pending",
            SendError: string.Empty,
            ServerState: "present",
            Lines: lines
                .Select(x => new AgentSentReportLine(x.Id, x.Line.ClientLabel, x.Line.Finished, x.Line.Hours))
                .ToList(),
            Days: draft.Days);

        return new ReportSendAssembly(
            Ok: true,
            Reason: null,
            Refusals: null,
            Json: JsonSerializer.Serialize(payload),
            ReportId: reportId,
            RecipientEmail: recipientEmail,
            Record: record);
    }

    private static LocalApiDailyGoal? ToApiGoal(AgentDailyGoal? goal)
    {
        return goal is null
            ? null
            : new LocalApiDailyGoal(
                LocalDate: goal.LocalDate,
                GoalText: goal.GoalText,
                TrackingPref: goal.TrackingPref,
                MiddayAnswer: goal.MiddayAnswer,
                PlanTomorrow: goal.PlanTomorrow,
                CreatedAtUtc: goal.CreatedAtUtc,
                MiddayAtUtc: goal.MiddayAtUtc,
                EndOfDayAtUtc: goal.EndOfDayAtUtc);
    }
}
