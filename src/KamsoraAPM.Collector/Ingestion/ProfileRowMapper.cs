// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Profiles.V1;
using KamsoraAPM.Storage.Models;

namespace KamsoraAPM.Collector.Ingestion;

/// <summary>
/// Converts an inbound protobuf <see cref="Profile"/> + its enclosing
/// <see cref="ResourceProfiles"/> + <see cref="ScopeProfiles"/> into a
/// storage-layer <see cref="ProfileRow"/>.
/// </summary>
internal static class ProfileRowMapper
{
    private const string AttrServiceName      = "service.name";
    private const string AttrServiceNamespace = "service.namespace";
    private const string AttrAgentVersion     = "kamsora.agent.version";

    public static ProfileRow ToRow(Guid tenantId, ResourceProfiles resourceProfiles, ScopeProfiles scope, Profile profile)
    {
        var (serviceName, serviceNamespace, agentVersion) =
            ExtractResourceAttributes(resourceProfiles.Resource);

        var row = new ProfileRow
        {
            TenantId          = tenantId,
            StartTimeUnixNano = profile.StartTimeUnixNano,
            DurationNanos     = profile.DurationNano,
            ServiceName       = serviceName,
            ServiceNamespace  = serviceNamespace,
            ProfileKind       = KindToString(profile.Kind),
            SampleCount       = profile.SampleCount,
            PprofBytes        = profile.Pprof.ToByteArray(),
            // Profile.trigger_trace_id is 16 raw bytes (per kamsora.profiles.v1).
            // Store the hex lowercase to match how spans/logs already store it.
            TriggerTraceIdHex = profile.TriggerTraceId.Length == 16
                ? Convert.ToHexString(profile.TriggerTraceId.ToByteArray()).ToLowerInvariant()
                : string.Empty,
            AgentVersion      = agentVersion,
        };

        if (profile.Attributes.Count > 0)
        {
            var keys   = new string[profile.Attributes.Count];
            var values = new string[profile.Attributes.Count];
            for (int i = 0; i < profile.Attributes.Count; i++)
            {
                keys[i]   = profile.Attributes[i].Key;
                values[i] = AnyValueToString(profile.Attributes[i].Value);
            }
            row.AttrsKeys   = keys;
            row.AttrsValues = values;
        }

        return row;
    }

    private static (string serviceName, string serviceNamespace, string agentVersion)
        ExtractResourceAttributes(Resource? resource)
    {
        string serviceName = string.Empty, serviceNamespace = string.Empty, agentVersion = string.Empty;
        if (resource is null) return (serviceName, serviceNamespace, agentVersion);

        foreach (var kv in resource.Attributes)
        {
            var v = AnyValueToString(kv.Value);
            switch (kv.Key)
            {
                case AttrServiceName:      serviceName      = v; break;
                case AttrServiceNamespace: serviceNamespace = v; break;
                case AttrAgentVersion:     agentVersion     = v; break;
            }
        }
        return (serviceName, serviceNamespace, agentVersion);
    }

    private static string KindToString(ProfileKind kind) => kind switch
    {
        ProfileKind.Cpu    => "CPU",
        ProfileKind.Wall   => "WALL",
        ProfileKind.Alloc  => "ALLOC",
        ProfileKind.Lock   => "LOCK",
        ProfileKind.Gc     => "GC",
        _                  => "UNSPECIFIED",
    };

    private static string AnyValueToString(AnyValue? value) => value?.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.IntValue    => value.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        AnyValue.ValueOneofCase.BoolValue   => value.BoolValue ? "true" : "false",
        AnyValue.ValueOneofCase.BytesValue  => Convert.ToBase64String(value.BytesValue.Span),
        _                                   => string.Empty,
    };
}
