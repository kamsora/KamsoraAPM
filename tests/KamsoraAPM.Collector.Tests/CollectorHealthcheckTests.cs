// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KamsoraAPM.Collector.Tests;

public class CollectorHealthcheckTests : IClassFixture<CollectorHealthcheckTests.NoStartupDependenciesFactory>
{
    private readonly NoStartupDependenciesFactory _factory;

    public CollectorHealthcheckTests(NoStartupDependenciesFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_returns_200_ok()
    {
        var client = _factory.CreateClient();
        var resp   = await client.GetAsync("/healthz");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body   = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("kamsora-apm-collector");
    }

    // The Collector's startup registers hosted services (ClickHouse migration
    // runner, batch flushers, retention sweeper) that reach ClickHouse/Postgres
    // when the host starts. /healthz is a pure liveness probe with no store
    // dependency, so for this smoke test we drop those hosted services - the
    // host then starts without any database available.
    public sealed class NoStartupDependenciesFactory : WebApplicationFactory<KamsoraAPM.Collector.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
