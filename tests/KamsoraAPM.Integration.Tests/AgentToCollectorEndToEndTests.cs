// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using ClickHouse.Client.ADO;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using KamsoraAPM.Contracts.Collector.V1;
using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Trace.V1;
using KamsoraAPM.Integration.Tests.Fixtures;
using Xunit;
using SpanStatus = KamsoraAPM.Contracts.Trace.V1.Status;

namespace KamsoraAPM.Integration.Tests;

/// <summary>
/// End-to-end M1 acceptance test. Sends a single span via gRPC against a real
/// in-process Collector backed by Testcontainers ClickHouse + PostgreSQL, then
/// reads the row back to verify the full Agent → Collector → ClickHouse path.
/// </summary>
[Collection(nameof(KamsoraStackCollection))]
public class AgentToCollectorEndToEndTests
{
    private readonly KamsoraStackFixture _stack;

    public AgentToCollectorEndToEndTests(KamsoraStackFixture stack) => _stack = stack;

    [Fact]
    public async Task A_span_sent_via_gRPC_is_persisted_to_ClickHouse_and_queryable()
    {
        // ---- arrange: client + payload --------------------------------------
        using var httpClient = _stack.CollectorHttpClient;
        using var channel    = GrpcChannel.ForAddress(_stack.CollectorBaseAddress, new GrpcChannelOptions
        {
            HttpClient = httpClient,
        });
        var client = new TraceService.TraceServiceClient(channel);

        var traceId = new byte[16];
        var spanId  = new byte[8];
        Random.Shared.NextBytes(traceId);
        Random.Shared.NextBytes(spanId);

        var nowNs = (ulong)((DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100L);

        var span = new Span
        {
            TraceId           = ByteString.CopyFrom(traceId),
            SpanId            = ByteString.CopyFrom(spanId),
            Name              = "GET /integration",
            Kind              = Span.Types.SpanKind.Server,
            StartTimeUnixNano = nowNs,
            EndTimeUnixNano   = nowNs + 5_000_000UL, // +5 ms
            Status            = new SpanStatus { Code = SpanStatus.Types.StatusCode.Ok },
        };
        span.Attributes.Add(new KeyValue { Key = "http.request.method",      Value = new AnyValue { StringValue = "GET" } });
        span.Attributes.Add(new KeyValue { Key = "http.route",               Value = new AnyValue { StringValue = "/integration" } });
        span.Attributes.Add(new KeyValue { Key = "http.response.status_code",Value = new AnyValue { IntValue   = 200 } });

        var resource = new Resource();
        resource.Attributes.Add(new KeyValue { Key = "service.name",            Value = new AnyValue { StringValue = "integration-svc" } });
        resource.Attributes.Add(new KeyValue { Key = "kamsora.agent.version",   Value = new AnyValue { StringValue = "0.1.0-test" } });

        var scope = new ScopeSpans { Scope = new InstrumentationScope { Name = "test", Version = "1.0" } };
        scope.Spans.Add(span);

        var resourceSpans = new ResourceSpans { Resource = resource };
        resourceSpans.ScopeSpans.Add(scope);

        var request = new ExportTraceRequest();
        request.ResourceSpans.Add(resourceSpans);

        var headers = new Metadata
        {
            { "x-kamsora-tenant",  _stack.TenantUuid },
            { "x-kamsora-api-key", _stack.ApiKey     },
        };

        // ---- act: send the span ---------------------------------------------
        var response = await client.ExportAsync(request, new CallOptions(headers: headers, deadline: DateTime.UtcNow.AddSeconds(10)));
        response.PartialSuccess.Should().BeNull("Collector should accept the span without partial-success rejection");

        // ---- assert: poll ClickHouse until the row materialises --------------
        // The Collector batches with a 250 ms flush interval (set by the fixture),
        // so the row should appear within a couple of seconds.
        var foundRow = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !foundRow)
        {
            await Task.Delay(250);

            await using var conn = new ClickHouseConnection(_stack.ClickHouseConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count() FROM kamsora_apm.spans WHERE tenant_id = {t:UUID} AND span_name = 'GET /integration'";
            var p = cmd.CreateParameter();
            p.ParameterName = "t";
            p.Value         = Guid.Parse(_stack.TenantUuid);
            cmd.Parameters.Add(p);

            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
            foundRow = count >= 1;
        }

        foundRow.Should().BeTrue("the span should be persisted to ClickHouse within the flush window");
    }

    [Fact]
    public async Task Calls_without_credentials_are_rejected_with_Unauthenticated()
    {
        using var httpClient = _stack.CollectorHttpClient;
        using var channel    = GrpcChannel.ForAddress(_stack.CollectorBaseAddress, new GrpcChannelOptions { HttpClient = httpClient });
        var client = new TraceService.TraceServiceClient(channel);

        var request = new ExportTraceRequest();
        request.ResourceSpans.Add(new ResourceSpans { Resource = new Resource() });

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.ExportAsync(request, new CallOptions(deadline: DateTime.UtcNow.AddSeconds(5))).ResponseAsync);

        ex.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }
}

[CollectionDefinition(nameof(KamsoraStackCollection))]
public class KamsoraStackCollection : ICollectionFixture<KamsoraStackFixture>
{
    // Marker class; xUnit shares the fixture across every [Collection]-marked
    // test class with this name. We do this so Testcontainers spin up once.
}
