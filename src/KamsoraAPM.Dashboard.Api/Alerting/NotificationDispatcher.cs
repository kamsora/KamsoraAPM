// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.Dashboard.Api.Alerting;

/// <summary>
/// The kind of transition we're notifying about. The dispatcher uses this to
/// shape the channel-specific payload (Slack header colour, webhook event
/// field, etc).
/// </summary>
public enum NotificationKind
{
    Firing,
    Resolved,
    /// <summary>Operator-triggered test from the Channels UI. No firing row created.</summary>
    Test,
}

/// <summary>
/// Snapshot of everything a channel handler needs to format an outbound
/// notification. Engine builds this once per (rule, transition) and reuses
/// across fan-out to N channels.
/// </summary>
public sealed record NotificationContext(
    Guid     TenantId,
    string   TenantSlug,
    string   RuleUuid,
    string   RuleName,
    string?  RuleDescription,
    string   SignalType,
    string   Operator,
    double   Threshold,
    double   ObservedValue,
    string   Severity,
    NotificationKind Kind,
    string?  FiringUuid,
    DateTime EventAtUtc);

public interface INotificationDispatcher
{
    /// <summary>Channel type this dispatcher handles (matches <c>masteralert_channels.channel_type</c>).</summary>
    string ChannelType { get; }

    Task DispatchAsync(ChannelDefinition channel, NotificationContext context, CancellationToken ct);
}

/// <summary>Generic webhook: POST JSON body to <c>config.url</c>.</summary>
public sealed class WebhookNotificationDispatcher : INotificationDispatcher
{
    public string ChannelType => ChannelTypes.Webhook;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookNotificationDispatcher> _logger;

    public WebhookNotificationDispatcher(IHttpClientFactory httpClientFactory, ILogger<WebhookNotificationDispatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task DispatchAsync(ChannelDefinition channel, NotificationContext context, CancellationToken ct)
    {
        var ctx = context;
        WebhookConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<WebhookConfig>(channel.ConfigJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Alerting: webhook channel {ChannelUuid} has invalid config_json; skipping.", channel.SysChannelUuid);
            return;
        }

        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Url))
        {
            _logger.LogWarning("Alerting: webhook channel {ChannelUuid} missing 'url' in config; skipping.", channel.SysChannelUuid);
            return;
        }

        var body = new
        {
            kind          = ctx.Kind.ToString().ToLowerInvariant(),
            severity      = ctx.Severity,
            tenant_id     = ctx.TenantId.ToString(),
            tenant_slug   = ctx.TenantSlug,
            rule_uuid     = ctx.RuleUuid,
            rule_name     = ctx.RuleName,
            description   = ctx.RuleDescription,
            signal        = ctx.SignalType,
            @operator     = ctx.Operator,
            threshold     = ctx.Threshold,
            observed      = ctx.ObservedValue,
            firing_uuid   = ctx.FiringUuid,
            event_at_utc  = ctx.EventAtUtc.ToString("O"),
        };

        var client  = _httpClientFactory.CreateClient("alerting-webhook");
        client.Timeout = TimeSpan.FromSeconds(10);
        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Url) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(cfg.SecretHeaderValue))
        {
            req.Headers.TryAddWithoutValidation(
                string.IsNullOrWhiteSpace(cfg.SecretHeaderName) ? "X-Kamsora-Alert-Secret" : cfg.SecretHeaderName,
                cfg.SecretHeaderValue);
        }

        try
        {
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Alerting: webhook {Url} responded {Status} for rule {Rule}.",
                    cfg.Url, (int)resp.StatusCode, ctx.RuleName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alerting: webhook POST to {Url} failed for rule {Rule}.", cfg.Url, ctx.RuleName);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class WebhookConfig
    {
        public string  Url               { get; set; } = string.Empty;
        public string? SecretHeaderName  { get; set; }
        public string? SecretHeaderValue { get; set; }
    }
}

/// <summary>
/// "In-dashboard" channel: writes a row to <c>tblapm_inapp_notifications</c>
/// which the dashboard polls. No external egress; works without any operator
/// setup. Always the default channel created at tenant onboarding.
/// </summary>
public sealed class InAppNotificationDispatcher : INotificationDispatcher
{
    public string ChannelType => ChannelTypes.InApp;

    private readonly AlertingRepository _repo;
    private readonly ILogger<InAppNotificationDispatcher> _logger;

    public InAppNotificationDispatcher(AlertingRepository repo, ILogger<InAppNotificationDispatcher> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    public async Task DispatchAsync(ChannelDefinition channel, NotificationContext context, CancellationToken ct)
    {
        var ctx = context;
        // Skip resolution notifications - banner is meant to surface what's
        // BROKEN; the firing row update + visual disappearance handles "all clear".
        if (ctx.Kind == NotificationKind.Resolved) return;
        if (ctx.FiringUuid is null && ctx.Kind != NotificationKind.Test) return;

        var title = $"{HumanSignal(ctx.SignalType)} {HumanOperator(ctx.Operator)} {FormatValue(ctx.SignalType, ctx.Threshold)}";
        var body  = $"{ctx.RuleName} - observed {FormatValue(ctx.SignalType, ctx.ObservedValue)}.";

        try
        {
            await _repo.InsertInAppNotificationAsync(
                ctx.TenantId,
                ctx.FiringUuid ?? "test:" + Guid.NewGuid().ToString("N")[..8],
                ctx.RuleUuid,
                title, body, ctx.Severity,
                ctx.ObservedValue, ctx.Threshold, ctx.SignalType, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alerting: failed to write in-app notification for rule {Rule}.", ctx.RuleName);
        }
    }

    private static string HumanSignal(string signal) => signal switch
    {
        SignalTypes.LatencyP50    => "p50 latency",
        SignalTypes.LatencyP90    => "p90 latency",
        SignalTypes.LatencyP99    => "p99 latency",
        SignalTypes.ErrorRate     => "error rate",
        SignalTypes.RequestVolume => "request volume",
        _                          => signal,
    };

    private static string HumanOperator(string op) => op switch
    {
        "gt" => ">", "gte" => "≥", "lt" => "<", "lte" => "≤", "eq" => "=", _ => op,
    };

    private static string FormatValue(string signal, double value) => signal switch
    {
        SignalTypes.LatencyP50    or SignalTypes.LatencyP90 or SignalTypes.LatencyP99
            => $"{value:N0}ms",
        SignalTypes.ErrorRate
            => $"{(value * 100):N2}%",
        SignalTypes.RequestVolume
            => $"{value:N0}",
        _ => value.ToString("N2"),
    };
}

/// <summary>Resolves the right dispatcher for a channel by its type.</summary>
public sealed class NotificationDispatcherRegistry
{
    private readonly Dictionary<string, INotificationDispatcher> _byType;
    private readonly ILogger<NotificationDispatcherRegistry> _logger;

    public NotificationDispatcherRegistry(
        IEnumerable<INotificationDispatcher> dispatchers,
        ILogger<NotificationDispatcherRegistry> logger)
    {
        _byType = dispatchers.ToDictionary(d => d.ChannelType, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public async Task DispatchAsync(
        ChannelDefinition channel, NotificationContext ctx, CancellationToken ct)
    {
        if (!_byType.TryGetValue(channel.ChannelType, out var dispatcher))
        {
            _logger.LogInformation("Alerting: no dispatcher registered for channel type '{Type}' - skipping.", channel.ChannelType);
            return;
        }
        await dispatcher.DispatchAsync(channel, ctx, ct).ConfigureAwait(false);
    }
}
