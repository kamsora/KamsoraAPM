// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using KamsoraAPM.Storage.Abstractions;
using KamsoraAPM.Storage.ClickHouse;
using KamsoraAPM.Storage.Options;
using KamsoraAPM.Storage.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KamsoraAPM.Storage.Extensions;

/// <summary>DI registration helpers for the KamsoraAPM Storage layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the ClickHouse + PostgreSQL data layer used by the Collector
    /// (write path) and the Dashboard.Api (read path). Configuration sections:
    /// <c>KamsoraApm:ClickHouse</c>, <c>KamsoraApm:Postgres</c>.
    /// </summary>
    public static IServiceCollection AddKamsoraStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ClickHouseOptions>()
                .Bind(configuration.GetSection("KamsoraApm:ClickHouse"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.AddOptions<PostgresOptions>()
                .Bind(configuration.GetSection("KamsoraApm:Postgres"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

        services.TryAddSingleton<ISpanWriter, ClickHouseSpanWriter>();
        services.TryAddSingleton<ISpanReader, ClickHouseSpanReader>();
        services.TryAddSingleton<IInsightsReader, ClickHouseInsightsReader>();
        services.TryAddSingleton<IHostSnapshotWriter, ClickHouseHostCpuMemoryWriter>();
        services.TryAddSingleton<IHostReader, ClickHouseHostReader>();
        services.TryAddSingleton<ClickHouseConsumerReader>();
        services.TryAddSingleton<IConsumerReader>(sp => sp.GetRequiredService<ClickHouseConsumerReader>());
        services.TryAddSingleton<IErrorsReader>(sp => sp.GetRequiredService<ClickHouseConsumerReader>());
        services.TryAddSingleton<ILogWriter,    ClickHouseLogWriter>();
        services.TryAddSingleton<IMetricWriter, ClickHouseMetricWriter>();
        services.TryAddSingleton<ILogReader,    ClickHouseLogReader>();
        services.TryAddSingleton<IMetricReader, ClickHouseMetricReader>();
        services.TryAddSingleton<IProfileWriter, ClickHouseProfileWriter>();
        services.TryAddSingleton<IProfileReader, ClickHouseProfileReader>();
        services.TryAddSingleton<IServiceMapReader, ClickHouseServiceMapReader>();
        services.TryAddSingleton<ITenantResolver, PostgresTenantResolver>();

        return services;
    }
}
