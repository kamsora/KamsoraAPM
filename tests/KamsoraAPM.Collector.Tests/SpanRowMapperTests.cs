// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using KamsoraAPM.Collector.Ingestion;
using KamsoraAPM.Contracts.Common.V1;
using KamsoraAPM.Contracts.Trace.V1;
using Xunit;

namespace KamsoraAPM.Collector.Tests;

/// <summary>
/// SpanRowMapper hoists db.* attributes into dedicated columns at ingestion.
/// A span only classifies as a "DB call" downstream (trace summary, Service Map
/// database edges, Overview DB stats) when db_system is populated, so the mapper
/// must accept BOTH the legacy (db.system / db.statement) and the stabilized
/// (db.system.name / db.query.text) OpenTelemetry database conventions - otherwise
/// spans from newer drivers like Npgsql 9+ get mis-counted as plain CLIENT calls.
/// </summary>
public class SpanRowMapperTests
{
    [Theory]
    [InlineData("db.system")]       // legacy OTel DB semconv (Npgsql 6-8, SqlClient)
    [InlineData("db.system.name")]  // stable OTel DB semconv (Npgsql 9+)
    public void Hoists_db_system_from_either_convention(string key)
    {
        var span = ClientSpan();
        span.Attributes.Add(Attr(key, "postgresql"));

        var row = SpanRowMapper.ToRow(Guid.NewGuid(), new ResourceSpans(), new ScopeSpans(), span);

        row.DbSystem.Should().Be("postgresql");
        row.AttrsKeys.Should().NotContain(key, "a hoisted attribute must not also land in the residual array");
    }

    [Theory]
    [InlineData("db.statement")]    // legacy
    [InlineData("db.query.text")]   // stable
    public void Hoists_db_statement_from_either_convention(string key)
    {
        const string sql = "SELECT * FROM master_plant WHERE company_id = $1";
        var span = ClientSpan();
        span.Attributes.Add(Attr(key, sql));

        var row = SpanRowMapper.ToRow(Guid.NewGuid(), new ResourceSpans(), new ScopeSpans(), span);

        row.DbStatement.Should().Be(sql);
    }

    [Fact]
    public void Dup_mode_keeps_a_single_stable_value()
    {
        // A driver opted into OTEL_SEMCONV_STABILITY_OPT_IN=database/dup emits both
        // keys; the row must still carry one stable db_system value.
        var span = ClientSpan();
        span.Attributes.Add(Attr("db.system", "postgresql"));
        span.Attributes.Add(Attr("db.system.name", "postgresql"));

        var row = SpanRowMapper.ToRow(Guid.NewGuid(), new ResourceSpans(), new ScopeSpans(), span);

        row.DbSystem.Should().Be("postgresql");
    }

    private static Span ClientSpan() => new()
    {
        Name = "postgresql",
        Kind = Span.Types.SpanKind.Client,
    };

    private static KeyValue Attr(string key, string value) =>
        new() { Key = key, Value = new AnyValue { StringValue = value } };
}
