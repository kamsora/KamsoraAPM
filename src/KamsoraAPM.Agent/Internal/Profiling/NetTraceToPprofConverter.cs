// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Google.Protobuf;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using PprofProfile = KamsoraAPM.Contracts.Pprof.Profile;
using PprofFunction = KamsoraAPM.Contracts.Pprof.Function;
using PprofLocation = KamsoraAPM.Contracts.Pprof.Location;
using PprofLine = KamsoraAPM.Contracts.Pprof.Line;
using PprofSample = KamsoraAPM.Contracts.Pprof.Sample;
using PprofValueType = KamsoraAPM.Contracts.Pprof.ValueType;

namespace KamsoraAPM.Agent.Internal.Profiling;

/// <summary>
/// Converts an EventPipe nettrace capture (CPU sample profile) into a
/// pprof <see cref="PprofProfile"/> byte payload. The conversion uses
/// <see cref="TraceLog"/> for symbol resolution - every loaded module's
/// methods are materialised from the rundown events in the nettrace file
/// so stack frames carry real <c>System.Linq.Enumerable.Where[T]</c> names
/// rather than raw method IDs.
/// </summary>
internal sealed class NetTraceToPprofConverter
{
    /// <summary>
    /// Read <paramref name="nettracePath"/>, fold every CPU sample's stack
    /// into a pprof Profile, and return the serialized bytes. Returns
    /// (bytes, sampleCount). If the capture contains zero samples the
    /// returned bytes are still a structurally-valid (empty) pprof so
    /// downstream consumers don't have to special-case it.
    /// </summary>
    public static (byte[] Bytes, long SampleCount) Convert(
        string nettracePath, DateTime captureStartUtc, TimeSpan captureDuration)
    {
        // TraceLog needs an .etlx sidecar to query resolved stacks. It is
        // produced from the .nettrace once; subsequent opens reuse it.
        var etlxPath = TraceLog.CreateFromEventPipeDataFile(nettracePath);
        try
        {
            using var traceLog = new TraceLog(etlxPath);
            var source = traceLog.Events.GetSource();

            // Pprof string table: index 0 MUST be "".
            var strings = new StringTable();
            strings.Intern(string.Empty);
            var samplesIdx     = strings.Intern("samples");
            var countIdx       = strings.Intern("count");
            var cpuIdx         = strings.Intern("cpu");
            var nanosecondsIdx = strings.Intern("nanoseconds");

            // Function / location interning so the same managed method emits
            // exactly one Function row and exactly one Location row.
            var functionByName = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var locationByFunc = new Dictionary<ulong, ulong>();
            var functions      = new List<PprofFunction>();
            var locations      = new List<PprofLocation>();

            // Sample aggregation: identical stacks fold into one Sample row
            // with the count incremented. Stacks are keyed by the joined
            // location-id sequence (cheap, deterministic).
            var sampleByStack = new Dictionary<string, PprofSample>(StringComparer.Ordinal);
            long totalSamples = 0;

            // The .NET sample profiler emits SampleProfile events at ~10ms
            // intervals carrying the running thread's managed call stack.
            source.Clr.AddCallbackForEvents((TraceEvent data) =>
            {
                if (data.ProviderName != "Microsoft-DotNETCore-SampleProfiler") return;
                if (data.EventName    != "Thread/Sample"                    ) return;

                var stack = data.CallStack();
                if (stack is null) return;

                var locIds = WalkStack(stack, strings, functionByName, locationByFunc, functions, locations);
                if (locIds.Count == 0) return;

                var key = StackKey(locIds);
                if (!sampleByStack.TryGetValue(key, out var sample))
                {
                    sample = new PprofSample();
                    sample.LocationId.AddRange(locIds);
                    sample.Value.Add(0);
                    sampleByStack[key] = sample;
                }
                sample.Value[0] += 1;
                totalSamples    += 1;
            });

            source.Process();

            // Build the wrapping pprof Profile.
            var profile = new PprofProfile
            {
                TimeNanos     = ToUnixNanos(captureStartUtc),
                DurationNanos = (long)(captureDuration.TotalMilliseconds * 1_000_000),
                Period        = 9_700_000,  // sampler default ~9.7 ms
                PeriodType    = new PprofValueType { Type = cpuIdx, Unit = nanosecondsIdx },
            };
            profile.SampleType.Add(new PprofValueType { Type = samplesIdx, Unit = countIdx });

            profile.StringTable.AddRange(strings.Items);
            profile.Function   .AddRange(functions);
            profile.Location   .AddRange(locations);
            profile.Sample     .AddRange(sampleByStack.Values);

            return (profile.ToByteArray(), totalSamples);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private static List<ulong> WalkStack(
        TraceCallStack stack,
        StringTable strings,
        Dictionary<string, ulong> functionByName,
        Dictionary<ulong, ulong> locationByFunc,
        List<PprofFunction> functions,
        List<PprofLocation> locations)
    {
        // pprof convention: leaf frame is first in the list, root last.
        // TraceCallStack chains parent via .Caller and gives the leaf first
        // when walked from the top - which is what we want.
        var locIds = new List<ulong>();
        for (var frame = stack; frame is not null; frame = frame.Caller)
        {
            var name = frame.CodeAddress?.FullMethodName;
            if (string.IsNullOrEmpty(name)) continue;

            if (!functionByName.TryGetValue(name, out var fnId))
            {
                fnId = (ulong)(functions.Count + 1);
                var nameIdx = strings.Intern(name);
                functions.Add(new PprofFunction
                {
                    Id         = fnId,
                    Name       = nameIdx,
                    SystemName = nameIdx,
                });
                functionByName[name] = fnId;
            }

            if (!locationByFunc.TryGetValue(fnId, out var locId))
            {
                locId = (ulong)(locations.Count + 1);
                var loc = new PprofLocation { Id = locId };
                loc.Line.Add(new PprofLine { FunctionId = fnId });
                locations.Add(loc);
                locationByFunc[fnId] = locId;
            }

            locIds.Add(locId);
        }
        return locIds;
    }

    private static string StackKey(List<ulong> locIds)
    {
        // Compact, allocation-light stack identifier. The exact bytes don't
        // matter - only that identical sequences hash to the same string.
        return string.Join(',', locIds);
    }

    private static long ToUnixNanos(DateTime utc)
    {
        const long UnixEpochTicks = 621_355_968_000_000_000L;
        var ticks = utc.Kind == DateTimeKind.Utc ? utc.Ticks : utc.ToUniversalTime().Ticks;
        return (ticks - UnixEpochTicks) * 100L;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException)            { /* benign - temp file */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>String interner backing the pprof string_table.</summary>
    private sealed class StringTable
    {
        private readonly Dictionary<string, long> _index = new(StringComparer.Ordinal);
        public List<string> Items { get; } = new();

        public long Intern(string s)
        {
            if (_index.TryGetValue(s, out var idx)) return idx;
            idx = Items.Count;
            Items.Add(s);
            _index[s] = idx;
            return idx;
        }
    }
}
