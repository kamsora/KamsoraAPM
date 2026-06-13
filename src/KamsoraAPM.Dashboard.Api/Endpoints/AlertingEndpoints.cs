// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using KamsoraAPM.Dashboard.Api.Alerting;
using KamsoraAPM.Dashboard.Api.Auth;

namespace KamsoraAPM.Dashboard.Api.Endpoints;

/// <summary>
/// M7.1 alerting REST surface.
///   <list type="bullet">
///     <item><c>/api/v1/alerts/rules</c>        — CRUD (owner only).</item>
///     <item><c>/api/v1/alerts/channels</c>     — CRUD + test (owner only).</item>
///     <item><c>/api/v1/alerts/firings</c>      — history (any logged-in user).</item>
///     <item><c>/api/v1/alerts/notifications</c>— in-app notification banner stream + ack.</item>
///   </list>
/// </summary>
public static class AlertingEndpoints
{
    public static IEndpointRouteBuilder MapAlertingEndpoints(this IEndpointRouteBuilder app)
    {
        var any   = app.MapGroup("/api/v1/alerts").RequireAuthorization();
        var owner = app.MapGroup("/api/v1/alerts").RequireAuthorization(KamsoraPolicies.TenantOwner);

        // ---- Rules ----
        owner.MapGet("/rules", async (HttpContext http, AlertingRepository repo, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var rules = await repo.ListRulesForTenantAsync(tid, ct).ConfigureAwait(false);
            return Results.Ok(rules.Select(ToRuleDto).ToArray());
        });

        owner.MapPost("/rules", async (
            HttpContext http, CreateAlertRuleRequest req, AlertingRepository repo, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var validation = ValidateRule(req);
            if (validation is not null) return Results.BadRequest(new { error = validation });

            var uuid = await repo.InsertRuleAsync(
                tid, req.RuleName, req.Description,
                req.SignalType, NullIfEmpty(req.SignalParam), NullIfEmpty(req.ServiceFilter), req.Operator, req.Threshold,
                req.WindowSeconds, req.ForSeconds, req.Severity,
                req.ChannelUuids ?? Array.Empty<string>(),
                ActorTag(http), ct).ConfigureAwait(false);
            return Results.Ok(new { sysRuleTransId = uuid });
        });

        owner.MapPut("/rules/{ruleUuid}", async (
            HttpContext http, string ruleUuid, UpdateAlertRuleRequest req, AlertingRepository repo, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var validation = ValidateRule(req);
            if (validation is not null) return Results.BadRequest(new { error = validation });

            var rows = await repo.UpdateRuleAsync(
                tid, ruleUuid,
                req.RuleName, req.Description, req.Enabled,
                req.SignalType, NullIfEmpty(req.SignalParam), NullIfEmpty(req.ServiceFilter), req.Operator, req.Threshold,
                req.WindowSeconds, req.ForSeconds, req.Severity,
                req.ChannelUuids ?? Array.Empty<string>(),
                ActorTag(http), ct).ConfigureAwait(false);
            return rows == 0 ? Results.NotFound() : Results.NoContent();
        });

        owner.MapDelete("/rules/{ruleUuid}", async (
            HttpContext http, string ruleUuid, AlertingRepository repo, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var rows = await repo.DeleteRuleAsync(tid, ruleUuid, ct).ConfigureAwait(false);
            return rows == 0 ? Results.NotFound() : Results.NoContent();
        });

        // ---- Channels ----
        owner.MapGet("/channels", async (HttpContext http, AlertingRepository repo, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var channels = await repo.ListChannelsForTenantAsync(tid, ct).ConfigureAwait(false);
            return Results.Ok(channels.Select(c => new AlertChannelDto(
                c.SysChannelUuid, c.ChannelName, c.ChannelType, c.ConfigJson, c.Enabled)).ToArray());
        });

        owner.MapPost("/channels", async (
            HttpContext http, CreateAlertChannelRequest req, AlertingRepository repo, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.ChannelName))
                return Results.BadRequest(new { error = "channelName is required" });
            if (req.ChannelType is not (ChannelTypes.Webhook or ChannelTypes.InApp))
                return Results.BadRequest(new { error = $"channelType must be one of: {ChannelTypes.Webhook}, {ChannelTypes.InApp}" });

            // Light shape validation per channel type.
            try { using var _ = JsonDocument.Parse(req.ConfigJson ?? "{}"); }
            catch (JsonException) { return Results.BadRequest(new { error = "configJson must be a valid JSON object." }); }

            var uuid = await repo.InsertChannelAsync(
                tid, req.ChannelName, req.ChannelType, req.ConfigJson ?? "{}", ActorTag(http), ct).ConfigureAwait(false);
            return Results.Ok(new { sysChannelUuid = uuid });
        });

        owner.MapDelete("/channels/{channelUuid}", async (
            HttpContext http, string channelUuid, AlertingRepository repo, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var rows = await repo.DeleteChannelAsync(tid, channelUuid, ct).ConfigureAwait(false);
            return rows == 0 ? Results.NotFound() : Results.NoContent();
        });

        // Channel test: synthesise a fake firing context and run the dispatcher
        // once. Doesn't write a firing row.
        owner.MapPost("/channels/{channelUuid}/test", async (
            HttpContext http, string channelUuid,
            AlertingRepository repo, NotificationDispatcherRegistry dispatchers, ITenantSlugLookup slugs,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var channels = await repo.ListChannelsForRuleAsync(tid, new[] { channelUuid }, ct).ConfigureAwait(false);
            if (channels.Count == 0) return Results.NotFound(new { error = "Channel not found or disabled." });

            var slug = slugs.LookupSlug(tid) ?? tid.ToString();
            var ctx  = new NotificationContext(
                tid, slug, RuleUuid: "test", RuleName: "KamsoraAPM test alert",
                RuleDescription: "Synthetic test notification from the Channels UI.",
                SignalType: SignalTypes.LatencyP99, Operator: "gt",
                Threshold: 500, ObservedValue: 712,
                Severity: Severities.Warning, Kind: NotificationKind.Test,
                FiringUuid: null, EventAtUtc: DateTime.UtcNow);
            await dispatchers.DispatchAsync(channels[0], ctx, ct).ConfigureAwait(false);
            return Results.Ok(new { delivered = true });
        });

        // ---- Firings + in-app notifications (viewable by any logged-in user) ----
        any.MapGet("/firings", async (
            HttpContext http, AlertingRepository repo,
            int? page, int? pageSize, bool? activeOnly,
            CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var list = await repo.ListFiringsAsync(tid, page ?? 1, pageSize ?? 50, activeOnly ?? false, ct).ConfigureAwait(false);
            var total = list.Count > 0 ? list[0].Total : 0;
            return Results.Ok(new
            {
                items = list.Select(f => new AlertFiringDto(
                    f.SysFiringTransId, f.SysRuleTransId, f.RuleName, f.SignalType,
                    f.FiredAtUtc, f.ResolvedAtUtc, f.ObservedValue, f.Severity)).ToArray(),
                total,
                page     = page     ?? 1,
                pageSize = pageSize ?? 50,
            });
        });

        any.MapGet("/notifications", async (
            HttpContext http, AlertingRepository repo, bool? unreadOnly, int? limit, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var list = await repo.ListInAppNotificationsAsync(tid, unreadOnly ?? false, limit ?? 50, ct).ConfigureAwait(false);
            return Results.Ok(list.Select(n => new InAppNotificationDto(
                n.SysNotificationTransId, n.SysRuleTransId, n.Title, n.Body, n.Severity,
                n.ObservedValue, n.Threshold, n.RuleSignal,
                n.AcknowledgedAtUtc, n.PostedAtUtc)).ToArray());
        });

        any.MapPost("/notifications/{notifUuid}/ack", async (
            HttpContext http, string notifUuid, AlertingRepository repo, CancellationToken ct) =>
        {
            if (!TryGetTenant(http, out var tid)) return Results.Unauthorized();
            var userUuid = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value ?? string.Empty;
            var rows = await repo.AcknowledgeNotificationAsync(tid, notifUuid, userUuid, ct).ConfigureAwait(false);
            return rows == 0 ? Results.NotFound() : Results.NoContent();
        });

        return app;
    }

    // ---- Helpers ---------------------------------------------------------

    private static bool TryGetTenant(HttpContext http, out Guid tenantId)
    {
        var claim = http.User.FindFirst(KamsoraClaimTypes.TenantId);
        if (claim is null || !Guid.TryParse(claim.Value, out tenantId))
        {
            tenantId = Guid.Empty;
            return false;
        }
        return true;
    }

    private static string ActorTag(HttpContext http)
    {
        var email = http.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value;
        return string.IsNullOrEmpty(email) ? "system:dashboard" : $"user:{email}";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? ValidateRule(AlertRulePayload req)
    {
        if (string.IsNullOrWhiteSpace(req.RuleName))    return "ruleName is required";
        if (!SignalTypes.IsSupported(req.SignalType))   return $"signalType must be one of: {SignalTypes.LatencyP50}, {SignalTypes.LatencyP90}, {SignalTypes.LatencyP99}, {SignalTypes.ErrorRate}, {SignalTypes.RequestVolume}, {SignalTypes.LogCount}, {SignalTypes.MetricAvg}, {SignalTypes.MetricMax}";
        if (SignalTypes.RequiresMetricName(req.SignalType) && string.IsNullOrWhiteSpace(req.SignalParam))
            return "signalParam (the metric name) is required for metric signals";
        if (req.SignalType == SignalTypes.LogCount
            && !string.IsNullOrWhiteSpace(req.SignalParam)
            && !LogSeverityFloors.IsValid(req.SignalParam))
            return "signalParam for log_count must be one of: TRACE, DEBUG, INFO, WARN, ERROR, FATAL";
        if (req.Operator is not ("gt" or "gte" or "lt" or "lte" or "eq"))
            return "operator must be one of: gt, gte, lt, lte, eq";
        if (!Severities.IsValid(req.Severity))          return "severity must be one of: info, warning, critical";
        if (req.WindowSeconds < 30 || req.WindowSeconds > 86400)
            return "windowSeconds must be between 30 and 86400";
        if (req.ForSeconds < 0 || req.ForSeconds > 86400)
            return "forSeconds must be between 0 and 86400";
        return null;
    }

    private static AlertRuleDto ToRuleDto(RuleDefinition r) => new(
        r.SysRuleTransId, r.RuleName, r.Description, r.Enabled,
        r.SignalType, r.SignalParam, r.ServiceFilter, r.Operator, r.Threshold,
        r.WindowSeconds, r.ForSeconds, r.Severity,
        r.ChannelUuids.ToArray(),
        r.LastState, r.LastPendingAtUtc, r.LastValue);
}

// ---- DTOs ------------------------------------------------------------------

public abstract record AlertRulePayload(
    string  RuleName,
    string? Description,
    string  SignalType,
    string? SignalParam,
    string? ServiceFilter,
    string  Operator,
    double  Threshold,
    int     WindowSeconds,
    int     ForSeconds,
    string  Severity,
    IReadOnlyList<string>? ChannelUuids);

public sealed record CreateAlertRuleRequest(
    string RuleName, string? Description,
    string SignalType, string? SignalParam, string? ServiceFilter,
    string Operator, double Threshold,
    int WindowSeconds, int ForSeconds,
    string Severity, IReadOnlyList<string>? ChannelUuids)
    : AlertRulePayload(RuleName, Description, SignalType, SignalParam, ServiceFilter, Operator, Threshold, WindowSeconds, ForSeconds, Severity, ChannelUuids);

public sealed record UpdateAlertRuleRequest(
    string RuleName, string? Description, bool Enabled,
    string SignalType, string? SignalParam, string? ServiceFilter,
    string Operator, double Threshold,
    int WindowSeconds, int ForSeconds,
    string Severity, IReadOnlyList<string>? ChannelUuids)
    : AlertRulePayload(RuleName, Description, SignalType, SignalParam, ServiceFilter, Operator, Threshold, WindowSeconds, ForSeconds, Severity, ChannelUuids);

public sealed record AlertRuleDto(
    string   SysRuleTransId,
    string   RuleName,
    string?  Description,
    bool     Enabled,
    string   SignalType,
    string?  SignalParam,
    string?  ServiceFilter,
    string   Operator,
    double   Threshold,
    int      WindowSeconds,
    int      ForSeconds,
    string   Severity,
    string[] ChannelUuids,
    string   LastState,
    DateTime? LastPendingAtUtc,
    double?  LastValue);

public sealed record CreateAlertChannelRequest(
    string  ChannelName,
    string  ChannelType,
    string? ConfigJson);

public sealed record AlertChannelDto(
    string SysChannelUuid,
    string ChannelName,
    string ChannelType,
    string ConfigJson,
    bool   Enabled);

public sealed record AlertFiringDto(
    string    SysFiringTransId,
    string    SysRuleTransId,
    string    RuleName,
    string    SignalType,
    DateTime  FiredAtUtc,
    DateTime? ResolvedAtUtc,
    double    ObservedValue,
    string    Severity);

public sealed record InAppNotificationDto(
    string    SysNotificationTransId,
    string    SysRuleTransId,
    string    Title,
    string    Body,
    string    Severity,
    double    ObservedValue,
    double    Threshold,
    string    RuleSignal,
    DateTime? AcknowledgedAtUtc,
    DateTime  PostedAtUtc);
