// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Google.Protobuf;
using KamsoraAPM.Contracts.Trace.V1;
using Xunit;

namespace KamsoraAPM.Contracts.Tests;

public class ContractsBuildSmokeTests
{
    [Fact]
    public void Span_default_instance_has_unspecified_kind()
    {
        var span = new Span();
        span.Kind.Should().Be(Span.Types.SpanKind.Unspecified);
        span.TraceId.Length.Should().Be(0);
    }

    [Fact]
    public void Span_roundtrips_through_protobuf()
    {
        var src = new Span
        {
            Name = "GET /api/users",
            Kind = Span.Types.SpanKind.Server,
            StartTimeUnixNano = 1_700_000_000_000_000_000UL,
            EndTimeUnixNano   = 1_700_000_000_005_000_000UL,
            Status            = new Status { Code = Status.Types.StatusCode.Ok }
        };

        var bytes = src.ToByteArray();
        var rt    = Span.Parser.ParseFrom(bytes);

        rt.Name.Should().Be("GET /api/users");
        rt.Kind.Should().Be(Span.Types.SpanKind.Server);
        (rt.EndTimeUnixNano - rt.StartTimeUnixNano).Should().Be(5_000_000UL);
        rt.Status.Code.Should().Be(Status.Types.StatusCode.Ok);
    }
}
