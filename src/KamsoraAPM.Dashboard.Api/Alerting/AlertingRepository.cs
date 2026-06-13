// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using KamsoraAPM.Storage.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace KamsoraAPM.Dashboard.Api.Alerting;

/// <summary>
/// Postgres I/O for the alerting engine. Wraps the Kamsora-pattern audit columns
/// so callers (engine + endpoints) don't repeat connection-string + parameter
/// glue.
/// </summary>
public sealed class AlertingRepository
{
    private readonly PostgresOptions _options;

    public AlertingRepository(IOptions<PostgresOptions> options) => _options = options.Value;

    public async Task<IReadOnlyList<RuleDefinition>> ListAllEnabledRulesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            SELECT sysruletransid, systenantuuid, rule_name, description, enabled,
                   signal_type, signal_param, service_filter, operator, threshold,
                   window_seconds, for_seconds, severity, channel_uuids,
                   last_state, last_pending_at, last_value
              FROM public.tblapm_alert_rules
             WHERE enabled = true", conn);

        var list = new List<RuleDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadRule(reader));
        }
        return list;
    }

    public async Task<IReadOnlyList<RuleDefinition>> ListRulesForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            SELECT sysruletransid, systenantuuid, rule_name, description, enabled,
                   signal_type, signal_param, service_filter, operator, threshold,
                   window_seconds, for_seconds, severity, channel_uuids,
                   last_state, last_pending_at, last_value
              FROM public.tblapm_alert_rules
             WHERE systenantuuid = @tenant
             ORDER BY posteddatetime DESC", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());

        var list = new List<RuleDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadRule(reader));
        }
        return list;
    }

    public async Task<RuleDefinition?> GetRuleAsync(Guid tenantId, string ruleUuid, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            SELECT sysruletransid, systenantuuid, rule_name, description, enabled,
                   signal_type, signal_param, service_filter, operator, threshold,
                   window_seconds, for_seconds, severity, channel_uuids,
                   last_state, last_pending_at, last_value
              FROM public.tblapm_alert_rules
             WHERE systenantuuid = @tenant AND sysruletransid = @uuid", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        cmd.Parameters.AddWithValue("uuid",   ruleUuid);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return ReadRule(reader);
    }

    public async Task<string> InsertRuleAsync(
        Guid tenantId, string ruleName, string? description,
        string signalType, string? signalParam, string? serviceFilter, string op, double threshold,
        int windowSeconds, int forSeconds, string severity,
        IReadOnlyList<string> channelUuids, string postedBy,
        CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(
            "SELECT public.fn_api_post_tblapm_alert_rules(@tenant, @name, @desc, @signal, @param, @svc, @op, @threshold, @win, @for, @sev, @chans, @by)", conn);
        cmd.Parameters.AddWithValue("tenant",    tenantId.ToString());
        cmd.Parameters.AddWithValue("name",      ruleName);
        cmd.Parameters.AddWithValue("desc",      (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("signal",    signalType);
        cmd.Parameters.AddWithValue("param",     (object?)signalParam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("svc",       (object?)serviceFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("op",        op);
        cmd.Parameters.AddWithValue("threshold", threshold);
        cmd.Parameters.AddWithValue("win",       windowSeconds);
        cmd.Parameters.AddWithValue("for",       forSeconds);
        cmd.Parameters.AddWithValue("sev",       severity);
        cmd.Parameters.AddWithValue("chans",     channelUuids.ToArray());
        cmd.Parameters.AddWithValue("by",        postedBy);

        var uuid = (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return uuid;
    }

    public async Task<int> UpdateRuleAsync(
        Guid tenantId, string ruleUuid,
        string ruleName, string? description, bool enabled,
        string signalType, string? signalParam, string? serviceFilter, string op, double threshold,
        int windowSeconds, int forSeconds, string severity,
        IReadOnlyList<string> channelUuids, string updatedBy,
        CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            UPDATE public.tblapm_alert_rules
               SET rule_name = @name, description = @desc, enabled = @enabled,
                   signal_type = @signal, signal_param = @param, service_filter = @svc,
                   operator = @op, threshold = @threshold,
                   window_seconds = @win, for_seconds = @for,
                   severity = @sev, channel_uuids = @chans,
                   updatedby = @by, updateddatetime = CURRENT_TIMESTAMP
             WHERE systenantuuid = @tenant AND sysruletransid = @uuid", conn);
        cmd.Parameters.AddWithValue("tenant",    tenantId.ToString());
        cmd.Parameters.AddWithValue("uuid",      ruleUuid);
        cmd.Parameters.AddWithValue("name",      ruleName);
        cmd.Parameters.AddWithValue("desc",      (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("enabled",   enabled);
        cmd.Parameters.AddWithValue("signal",    signalType);
        cmd.Parameters.AddWithValue("param",     (object?)signalParam ?? DBNull.Value);
        cmd.Parameters.AddWithValue("svc",       (object?)serviceFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("op",        op);
        cmd.Parameters.AddWithValue("threshold", threshold);
        cmd.Parameters.AddWithValue("win",       windowSeconds);
        cmd.Parameters.AddWithValue("for",       forSeconds);
        cmd.Parameters.AddWithValue("sev",       severity);
        cmd.Parameters.AddWithValue("chans",     channelUuids.ToArray());
        cmd.Parameters.AddWithValue("by",        updatedBy);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteRuleAsync(Guid tenantId, string ruleUuid, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(
            "DELETE FROM public.tblapm_alert_rules WHERE systenantuuid = @tenant AND sysruletransid = @uuid", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        cmd.Parameters.AddWithValue("uuid",   ruleUuid);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateRuleStateAsync(
        string ruleUuid, string newState, DateTime? pendingAtUtc, double lastValue, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            UPDATE public.tblapm_alert_rules
               SET last_state         = @state,
                   last_pending_at    = @pending,
                   last_value         = @val,
                   last_evaluated_at  = CURRENT_TIMESTAMP,
                   updatedby          = 'system:alert-engine',
                   updateddatetime    = CURRENT_TIMESTAMP
             WHERE sysruletransid = @uuid", conn);
        cmd.Parameters.AddWithValue("state",    newState);
        cmd.Parameters.AddWithValue("pending",  (object?)pendingAtUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("val",      lastValue);
        cmd.Parameters.AddWithValue("uuid",     ruleUuid);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> InsertFiringAsync(
        Guid tenantId, string ruleUuid, double observedValue, string severity, string payloadJson,
        CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            INSERT INTO public.tblapm_alert_firings
                (systenantuuid, sysruletransid, fired_at, observed_value, severity, payload_json, posteddatetime, postedby)
            VALUES (@tenant, @rule, CURRENT_TIMESTAMP, @val, @sev, @payload::jsonb, CURRENT_TIMESTAMP, 'system:alert-engine')
            RETURNING sysfiringtransid", conn);
        cmd.Parameters.AddWithValue("tenant",  tenantId.ToString());
        cmd.Parameters.AddWithValue("rule",    ruleUuid);
        cmd.Parameters.AddWithValue("val",     observedValue);
        cmd.Parameters.AddWithValue("sev",     severity);
        cmd.Parameters.AddWithValue("payload", payloadJson);
        return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task ResolveOpenFiringAsync(string ruleUuid, double observedValue, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            UPDATE public.tblapm_alert_firings
               SET resolved_at = CURRENT_TIMESTAMP,
                   payload_json = jsonb_set(payload_json, '{resolved_value}', to_jsonb(@val::float8)),
                   updatedby = 'system:alert-engine',
                   updateddatetime = CURRENT_TIMESTAMP
             WHERE sysruletransid = @rule
               AND resolved_at IS NULL", conn);
        cmd.Parameters.AddWithValue("rule", ruleUuid);
        cmd.Parameters.AddWithValue("val",  observedValue);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChannelDefinition>> ListChannelsForRuleAsync(
        Guid tenantId, IReadOnlyList<string> channelUuids, CancellationToken ct)
    {
        if (channelUuids.Count == 0) return Array.Empty<ChannelDefinition>();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            SELECT syschanneluuid, systenantuuid, channel_name, channel_type, config_json::text, enabled
              FROM public.masteralert_channels
             WHERE systenantuuid = @tenant
               AND syschanneluuid = ANY(@uuids)
               AND enabled = true", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        cmd.Parameters.AddWithValue("uuids",  channelUuids.ToArray());

        var list = new List<ChannelDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ChannelDefinition(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? "{}" : reader.GetString(4),
                reader.GetBoolean(5)));
        }
        return list;
    }

    public async Task<IReadOnlyList<ChannelDefinition>> ListChannelsForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            SELECT syschanneluuid, systenantuuid, channel_name, channel_type, config_json::text, enabled
              FROM public.masteralert_channels
             WHERE systenantuuid = @tenant
             ORDER BY posteddatetime DESC", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());

        var list = new List<ChannelDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ChannelDefinition(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? "{}" : reader.GetString(4),
                reader.GetBoolean(5)));
        }
        return list;
    }

    public async Task<string> InsertChannelAsync(
        Guid tenantId, string channelName, string channelType, string configJson, string postedBy,
        CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            INSERT INTO public.masteralert_channels
                (systenantuuid, channel_name, channel_type, config_json, enabled, posteddatetime, postedby)
            VALUES (@tenant, @name, @type, @cfg::jsonb, true, CURRENT_TIMESTAMP, @by)
            RETURNING syschanneluuid", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        cmd.Parameters.AddWithValue("name",   channelName);
        cmd.Parameters.AddWithValue("type",   channelType);
        cmd.Parameters.AddWithValue("cfg",    configJson);
        cmd.Parameters.AddWithValue("by",     postedBy);
        return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task<int> DeleteChannelAsync(Guid tenantId, string channelUuid, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(
            "DELETE FROM public.masteralert_channels WHERE systenantuuid = @tenant AND syschanneluuid = @uuid", conn);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        cmd.Parameters.AddWithValue("uuid",   channelUuid);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FiringRow>> ListFiringsAsync(
        Guid tenantId, int page, int pageSize, bool activeOnly, CancellationToken ct)
    {
        var size   = Math.Clamp(pageSize, 1, 200);
        var offset = Math.Max(0, (Math.Max(1, page) - 1) * size);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var sql = $@"
            SELECT f.sysfiringtransid, f.sysruletransid, r.rule_name, r.signal_type,
                   f.fired_at, f.resolved_at, f.observed_value, f.severity,
                   count(*) OVER ()
              FROM public.tblapm_alert_firings f
              JOIN public.tblapm_alert_rules r ON r.sysruletransid = f.sysruletransid
             WHERE f.systenantuuid = @tenant
               {(activeOnly ? "AND f.resolved_at IS NULL" : "")}
             ORDER BY f.fired_at DESC
             LIMIT @size OFFSET @off";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        cmd.Parameters.AddWithValue("size",   size);
        cmd.Parameters.AddWithValue("off",    offset);

        var list = new List<FiringRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new FiringRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                reader.IsDBNull(5) ? null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                reader.GetDouble(6),
                reader.GetString(7),
                reader.GetInt64(8)));
        }
        return list;
    }

    public async Task<long> InsertInAppNotificationAsync(
        Guid tenantId, string firingUuid, string ruleUuid,
        string title, string body, string severity,
        double observedValue, double threshold, string signal,
        CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            INSERT INTO public.tblapm_inapp_notifications
                (systenantuuid, sysfiringtransid, sysruletransid,
                 title, body, severity,
                 observed_value, threshold, rule_signal,
                 posteddatetime, postedby)
            VALUES (@tenant, @firing, @rule,
                    @title, @body, @sev,
                    @val, @threshold, @signal,
                    CURRENT_TIMESTAMP, 'system:alert-engine')
            RETURNING notificationid", conn);
        cmd.Parameters.AddWithValue("tenant",    tenantId.ToString());
        cmd.Parameters.AddWithValue("firing",    firingUuid);
        cmd.Parameters.AddWithValue("rule",      ruleUuid);
        cmd.Parameters.AddWithValue("title",     title);
        cmd.Parameters.AddWithValue("body",      body);
        cmd.Parameters.AddWithValue("sev",       severity);
        cmd.Parameters.AddWithValue("val",       observedValue);
        cmd.Parameters.AddWithValue("threshold", threshold);
        cmd.Parameters.AddWithValue("signal",    signal);
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<InAppNotificationRow>> ListInAppNotificationsAsync(
        Guid tenantId, bool unreadOnly, int limit, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var sql = $@"
            SELECT sysnotificationtransid, sysruletransid, title, body, severity,
                   observed_value, threshold, rule_signal,
                   acknowledged_at, posteddatetime
              FROM public.tblapm_inapp_notifications
             WHERE systenantuuid = @tenant
               {(unreadOnly ? "AND acknowledged_at IS NULL" : "")}
             ORDER BY posteddatetime DESC
             LIMIT @lim";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        cmd.Parameters.AddWithValue("lim",    Math.Clamp(limit, 1, 100));

        var list = new List<InAppNotificationRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new InAppNotificationRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.IsDBNull(8) ? null : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)));
        }
        return list;
    }

    public async Task<int> AcknowledgeNotificationAsync(Guid tenantId, string notifUuid, string userUuid, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = new NpgsqlCommand(@"
            UPDATE public.tblapm_inapp_notifications
               SET acknowledged_at = CURRENT_TIMESTAMP,
                   acknowledged_useruuid = @user
             WHERE systenantuuid = @tenant
               AND sysnotificationtransid = @uuid
               AND acknowledged_at IS NULL", conn);
        cmd.Parameters.AddWithValue("user",   userUuid);
        cmd.Parameters.AddWithValue("tenant", tenantId.ToString());
        cmd.Parameters.AddWithValue("uuid",   notifUuid);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---- Helpers ---------------------------------------------------------

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static RuleDefinition ReadRule(System.Data.Common.DbDataReader r)
    {
        return new RuleDefinition(
            SysRuleTransId:    r.GetString(0),
            TenantId:          Guid.Parse(r.GetString(1)),
            RuleName:          r.GetString(2),
            Description:       r.IsDBNull(3) ? null : r.GetString(3),
            Enabled:           r.GetBoolean(4),
            SignalType:        r.GetString(5),
            SignalParam:       r.IsDBNull(6) ? null : r.GetString(6),
            ServiceFilter:     r.IsDBNull(7) ? null : r.GetString(7),
            Operator:          r.GetString(8),
            Threshold:         r.GetDouble(9),
            WindowSeconds:     r.GetInt32(10),
            ForSeconds:        r.GetInt32(11),
            Severity:          r.GetString(12),
            ChannelUuids:      r.IsDBNull(13) ? Array.Empty<string>() : (string[])r.GetValue(13),
            LastState:         r.IsDBNull(14) ? RuleStates.Ok : r.GetString(14),
            LastPendingAtUtc:  r.IsDBNull(15) ? null : DateTime.SpecifyKind(r.GetDateTime(15), DateTimeKind.Utc),
            LastValue:         r.IsDBNull(16) ? null : r.GetDouble(16));
    }
}

public sealed record FiringRow(
    string    SysFiringTransId,
    string    SysRuleTransId,
    string    RuleName,
    string    SignalType,
    DateTime  FiredAtUtc,
    DateTime? ResolvedAtUtc,
    double    ObservedValue,
    string    Severity,
    long      Total);

public sealed record InAppNotificationRow(
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
