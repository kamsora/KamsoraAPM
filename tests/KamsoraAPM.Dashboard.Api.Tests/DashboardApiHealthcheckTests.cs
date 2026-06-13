// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace KamsoraAPM.Dashboard.Api.Tests;

public class DashboardApiHealthcheckTests : IClassFixture<WebApplicationFactory<KamsoraAPM.Dashboard.Api.Program>>
{
    private readonly WebApplicationFactory<KamsoraAPM.Dashboard.Api.Program> _factory;

    public DashboardApiHealthcheckTests(WebApplicationFactory<KamsoraAPM.Dashboard.Api.Program> factory) => _factory = factory;

    [Fact]
    public async Task Healthz_returns_200_ok()
    {
        var client = _factory.CreateClient();
        var resp   = await client.GetAsync("/healthz");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body   = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("kamsora-apm-dashboard-api");
    }
}
