// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KamsoraAPM.Dashboard.Api.Alerting;

/// <summary>
/// M7.1 alert engine. Every 60 seconds:
///   <list type="number">
///     <item>Load all enabled rules from Postgres (one query, all tenants).</item>
///     <item>For each rule, query its current signal value from ClickHouse.</item>
///     <item>Drive the state machine (ok ↔ pending ↔ firing) using the rule's
///       <c>for_seconds</c> sustained-violation requirement.</item>
///     <item>On transition only, write to Postgres (firing row + rule state)
///       and fan out to configured channels.</item>
///   </list>
///
/// State is held in-memory in <see cref="_state"/>; restart-safe because
/// <c>last_state</c> + <c>last_pending_at</c> persist in the rule row, and
/// we re-hydrate them on the first iteration.
/// </summary>
public sealed class AlertEngineHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private readonly AlertingRepository _repo;
    private readonly RuleSignalQuerier _querier;
    private readonly NotificationDispatcherRegistry _dispatchers;
    private readonly ITenantSlugLookup _slugs;
    private readonly ILogger<AlertEngineHostedService> _logger;
    private readonly ConcurrentDictionary<string, EvaluatorState> _state = new(StringComparer.Ordinal);

    public AlertEngineHostedService(
        AlertingRepository repo,
        RuleSignalQuerier querier,
        NotificationDispatcherRegistry dispatchers,
        ITenantSlugLookup slugs,
        ILogger<AlertEngineHostedService> logger)
    {
        _repo        = repo;
        _querier     = querier;
        _dispatchers = dispatchers;
        _slugs       = slugs;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("KamsoraAPM alert engine starting (interval={Interval}).", TickInterval);

        // First tick on a 5-second delay so the API has finished startup and
        // ClickHouse/Postgres are reachable before we hit them.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KamsoraAPM alert engine tick failed; retrying on next interval.");
            }

            try { await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }

        _logger.LogInformation("KamsoraAPM alert engine stopping.");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var rules  = await _repo.ListAllEnabledRulesAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("Alert engine: evaluating {Count} enabled rule(s).", rules.Count);

        foreach (var rule in rules)
        {
            if (!SignalTypes.IsSupported(rule.SignalType)) continue;
            try
            {
                await EvaluateRuleAsync(rule, nowUtc, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alert engine: rule {RuleName} ({Uuid}) evaluation failed.", rule.RuleName, rule.SysRuleTransId);
            }
        }

        // Cleanup: rules that were deleted/disabled lose their in-memory state.
        var liveUuids = rules.Select(r => r.SysRuleTransId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _state.Keys.Where(k => !liveUuids.Contains(k)).ToList())
        {
            _state.TryRemove(stale, out _);
        }
    }

    private async Task EvaluateRuleAsync(RuleDefinition rule, DateTime nowUtc, CancellationToken ct)
    {
        var current = _state.GetOrAdd(rule.SysRuleTransId, _ => new EvaluatorState
        {
            State        = rule.LastState ?? RuleStates.Ok,
            PendingAtUtc = rule.LastPendingAtUtc,
        });

        var observed  = await _querier.QueryAsync(rule, nowUtc, ct).ConfigureAwait(false);
        var breached  = RuleSignalQuerier.CompareThreshold(rule.Operator, observed, rule.Threshold);

        // State transitions ------------------------------------------------
        string? newState        = null;
        DateTime? newPendingAt  = current.PendingAtUtc;
        string? transitionKind  = null;   // "firing" or "resolved" — only set on edge

        switch (current.State)
        {
            case RuleStates.Ok:
                if (breached)
                {
                    newState     = RuleStates.Pending;
                    newPendingAt = nowUtc;
                    // If for_seconds = 0 the rule wants instant firing — fall through.
                    if (rule.ForSeconds <= 0)
                    {
                        newState       = RuleStates.Firing;
                        transitionKind = "firing";
                    }
                }
                break;

            case RuleStates.Pending:
                if (!breached)
                {
                    newState     = RuleStates.Ok;
                    newPendingAt = null;
                }
                else if (current.PendingAtUtc is not null
                         && (nowUtc - current.PendingAtUtc.Value).TotalSeconds >= rule.ForSeconds)
                {
                    newState       = RuleStates.Firing;
                    transitionKind = "firing";
                }
                break;

            case RuleStates.Firing:
                if (!breached)
                {
                    newState       = RuleStates.Ok;
                    newPendingAt   = null;
                    transitionKind = "resolved";
                }
                break;
        }

        if (newState is null)
        {
            // No change; still persist last_value periodically so the UI shows
            // a fresh observed reading. Avoid hammering DB — only update value.
            current.LastValueObserved = observed;
            return;
        }

        await _repo.UpdateRuleStateAsync(rule.SysRuleTransId, newState, newPendingAt, observed, ct).ConfigureAwait(false);
        current.State        = newState;
        current.PendingAtUtc = newPendingAt;
        current.LastValueObserved = observed;

        if (transitionKind == "firing")
        {
            await OnFiringAsync(rule, observed, nowUtc, ct).ConfigureAwait(false);
        }
        else if (transitionKind == "resolved")
        {
            await OnResolvedAsync(rule, observed, nowUtc, ct).ConfigureAwait(false);
        }
    }

    private async Task OnFiringAsync(RuleDefinition rule, double observed, DateTime nowUtc, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            signal     = rule.SignalType,
            @operator  = rule.Operator,
            threshold  = rule.Threshold,
            observed,
            service    = rule.ServiceFilter,
        });
        var firingUuid = await _repo.InsertFiringAsync(
            rule.TenantId, rule.SysRuleTransId, observed, rule.Severity, payload, ct).ConfigureAwait(false);

        await DispatchAsync(rule, observed, NotificationKind.Firing, firingUuid, nowUtc, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Alert FIRING: rule={RuleName} signal={Signal} observed={Observed} threshold={Threshold} severity={Severity}.",
            rule.RuleName, rule.SignalType, observed, rule.Threshold, rule.Severity);
    }

    private async Task OnResolvedAsync(RuleDefinition rule, double observed, DateTime nowUtc, CancellationToken ct)
    {
        await _repo.ResolveOpenFiringAsync(rule.SysRuleTransId, observed, ct).ConfigureAwait(false);
        await DispatchAsync(rule, observed, NotificationKind.Resolved, firingUuid: null, nowUtc, ct).ConfigureAwait(false);

        _logger.LogInformation("Alert RESOLVED: rule={RuleName} observed={Observed}.", rule.RuleName, observed);
    }

    private async Task DispatchAsync(
        RuleDefinition rule, double observed, NotificationKind kind, string? firingUuid, DateTime nowUtc,
        CancellationToken ct)
    {
        if (rule.ChannelUuids.Count == 0) return;

        var channels = await _repo.ListChannelsForRuleAsync(rule.TenantId, rule.ChannelUuids, ct).ConfigureAwait(false);
        if (channels.Count == 0) return;

        var slug = _slugs.LookupSlug(rule.TenantId) ?? rule.TenantId.ToString();
        var ctx  = new NotificationContext(
            rule.TenantId, slug,
            rule.SysRuleTransId, rule.RuleName, rule.Description,
            rule.SignalType, rule.Operator, rule.Threshold, observed,
            rule.Severity, kind, firingUuid, nowUtc);

        var tasks = channels.Select(c => _dispatchers.DispatchAsync(c, ctx, ct)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private sealed class EvaluatorState
    {
        public string State { get; set; } = RuleStates.Ok;
        public DateTime? PendingAtUtc { get; set; }
        public double LastValueObserved { get; set; }
    }
}

/// <summary>
/// Resolves a tenant's display slug (used in notification payloads). Implemented
/// by a small singleton cache that lazily reads from Postgres; avoids dragging
/// a full tenant repository into the engine.
/// </summary>
public interface ITenantSlugLookup
{
    string? LookupSlug(Guid tenantId);
}
