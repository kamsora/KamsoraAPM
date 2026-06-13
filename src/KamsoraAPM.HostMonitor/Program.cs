// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Grpc.Net.Client;
using KamsoraAPM.Contracts.Collector.V1;
using KamsoraAPM.HostMonitor.Beat;
using KamsoraAPM.HostMonitor.Options;
using KamsoraAPM.HostMonitor.Sampling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Host.CreateApplicationBuilder uses HostApplicationBuilder (not WebApplicationBuilder),
    // so the `(ctx, sp, cfg)` Serilog overload is unavailable. Build the static
    // logger from configuration and register it via the simple AddSerilog() extension.
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    builder.Services.AddSerilog();

    builder.Services.AddOptions<HostMonitorOptions>()
        .Bind(builder.Configuration.GetSection("KamsoraApm:HostMonitor"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    // Per-OS samplers. Linux impls are placeholders until M3.y.
    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddSingleton<ICpuMemorySampler, WindowsCpuMemorySampler>();
        builder.Services.AddSingleton<IDiskSampler,      WindowsDiskSampler>();
        builder.Services.AddSingleton<INetworkSampler,   WindowsNetworkSampler>();
        builder.Services.AddSingleton<IProcessSampler,   WindowsProcessSampler>();
    }
    else
    {
        builder.Services.AddSingleton<ICpuMemorySampler, LinuxCpuMemorySampler>();
        builder.Services.AddSingleton<IDiskSampler,      NullDiskSampler>();
        builder.Services.AddSingleton<INetworkSampler,   NullNetworkSampler>();
        builder.Services.AddSingleton<IProcessSampler,   NullProcessSampler>();
    }

    // gRPC client for the Collector's HostService. One channel per process.
    builder.Services.AddSingleton(sp =>
    {
        var opts    = sp.GetRequiredService<IOptions<HostMonitorOptions>>().Value;
        var channel = GrpcChannel.ForAddress(opts.CollectorEndpoint);
        return new HostService.HostServiceClient(channel);
    });

    builder.Services.AddHostedService<HostBeatService>();

    if (OperatingSystem.IsLinux())
    {
        builder.Services.AddSystemd();
    }
    else if (OperatingSystem.IsWindows())
    {
        builder.Services.AddWindowsService(o => o.ServiceName = "KamsoraAPM.HostMonitor");
    }

    var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "KamsoraAPM HostMonitor terminated unexpectedly.");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
