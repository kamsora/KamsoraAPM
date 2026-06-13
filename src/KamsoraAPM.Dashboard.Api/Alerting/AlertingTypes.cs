// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

namespace KamsoraAPM.Dashboard.Api.Alerting;

/// <summary>Hard list of signal types the alert engine knows how to evaluate.</summary>
public static class SignalTypes
{
    public const string LatencyP50    = "latency_p50";
    public const string LatencyP90    = "latency_p90";
    public const string LatencyP99    = "latency_p99";
    public const string ErrorRate     = "error_rate";
    public const string RequestVolume = "request_volume";
    /// <summary>Log records at/above a severity floor. SignalParam = floor name (default ERROR).</summary>
    public const string LogCount      = "log_count";
    /// <summary>Average of a metric's scalar value. SignalParam = metric name (required).</summary>
    public const string MetricAvg     = "metric_avg";
    /// <summary>Max of a metric's scalar value. SignalParam = metric name (required).</summary>
    public const string MetricMax     = "metric_max";

    public static bool IsSupported(string s) => s is
        LatencyP50 or LatencyP90 or LatencyP99 or ErrorRate or RequestVolume
        or LogCount or MetricAvg or MetricMax;

    /// <summary>Signals whose SignalParam must be a non-empty metric name.</summary>
    public static bool RequiresMetricName(string s) => s is MetricAvg or MetricMax;
}

/// <summary>OTLP severity floors for <see cref="SignalTypes.LogCount"/> rules.</summary>
public static class LogSeverityFloors
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TRACE"] = 1, ["DEBUG"] = 5, ["INFO"] = 9, ["WARN"] = 13, ["ERROR"] = 17, ["FATAL"] = 21,
    };

    public static bool IsValid(string name) => Map.ContainsKey(name);

    /// <summary>Floor number for a severity name; defaults to ERROR (17) when blank/unknown.</summary>
    public static int Resolve(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Map.TryGetValue(name, out var n) ? n : 17;
}

public static class RuleStates
{
    public const string Ok      = "ok";
    public const string Pending = "pending";
    public const string Firing  = "firing";
}

public static class ChannelTypes
{
    public const string Webhook   = "webhook";
    public const string InApp     = "inapp";
    // Slack/Email/PagerDuty arrive in M7.2.
}

/// <summary>A row read from <c>tblapm_alert_rules</c> for one rule.</summary>
public sealed record RuleDefinition(
    string   SysRuleTransId,
    Guid     TenantId,
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
    IReadOnlyList<string> ChannelUuids,
    string   LastState,
    DateTime? LastPendingAtUtc,
    double?  LastValue);

/// <summary>A row read from <c>masteralert_channels</c>.</summary>
public sealed record ChannelDefinition(
    string  SysChannelUuid,
    Guid    TenantId,
    string  ChannelName,
    string  ChannelType,
    string  ConfigJson,        // raw jsonb text, channel-specific
    bool    Enabled);

/// <summary>Result of evaluating one rule against the current ClickHouse window.</summary>
public sealed record SignalEvaluation(
    double ObservedValue,
    bool   ThresholdBreached);

/// <summary>Severity values for in-app notifications + firing rows.</summary>
public static class Severities
{
    public const string Info     = "info";
    public const string Warning  = "warning";
    public const string Critical = "critical";

    public static bool IsValid(string s) => s is Info or Warning or Critical;
}
