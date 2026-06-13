// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using KamsoraAPM.Dashboard.Api.Alerting;
using KamsoraAPM.Dashboard.Api.Auth;
using KamsoraAPM.Dashboard.Api.Bootstrap;
using KamsoraAPM.Dashboard.Api.Endpoints;
using KamsoraAPM.Dashboard.Api.Options;
using KamsoraAPM.Storage.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // M10/P0 — fail fast when a Production deployment still carries the
    // repository's development secrets (JWT key, DB passwords, seed admin).
    DevSecretsGuard.ThrowIfProductionWithDevSecrets(
        builder.Environment, builder.Configuration,
        "KamsoraApm:Auth:JwtSigningKey",
        "KamsoraApm:Auth:SeedTenant:AdminPassword");

    builder.Host.UseSerilog((ctx, _, loggerConfig) =>
    {
        loggerConfig
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    });

    builder.Services.AddOptions<DashboardAuthOptions>()
        .Bind(builder.Configuration.GetSection("KamsoraApm:Auth"))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddKamsoraStorage(builder.Configuration);
    builder.Services.AddSingleton<JwtIssuer>();
    builder.Services.AddHostedService<TenantSeederHostedService>();

    // M7 alerting --------------------------------------------------------
    builder.Services.AddHttpClient("alerting-webhook");
    builder.Services.AddSingleton<AlertingRepository>();
    builder.Services.AddSingleton<RuleSignalQuerier>();
    builder.Services.AddSingleton<ITenantSlugLookup, TenantSlugLookup>();
    builder.Services.AddSingleton<INotificationDispatcher, WebhookNotificationDispatcher>();
    builder.Services.AddSingleton<INotificationDispatcher, InAppNotificationDispatcher>();
    builder.Services.AddSingleton<NotificationDispatcherRegistry>();
    builder.Services.AddHostedService<AlertEngineHostedService>();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var auth = builder.Configuration.GetSection("KamsoraApm:Auth").Get<DashboardAuthOptions>() ?? new();
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidateAudience         = true,
                ValidateLifetime         = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer              = auth.JwtIssuer,
                ValidAudience            = auth.JwtAudience,
                IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                    string.IsNullOrEmpty(auth.JwtSigningKey)
                        ? new string('0', 32) // overridden post-bind; ValidateOnStart still enforces non-empty
                        : auth.JwtSigningKey)),
                ClockSkew                = TimeSpan.FromSeconds(30),
            };
        });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy(KamsoraPolicies.PlatformAdmin, p =>
            p.RequireAuthenticatedUser()
             .RequireClaim(KamsoraClaimTypes.PlatformAdmin, "true"));

        options.AddPolicy(KamsoraPolicies.TenantOwner, p =>
            p.RequireAuthenticatedUser()
             .RequireClaim(KamsoraClaimTypes.Role, "owner"));
    });
    builder.Services.AddHealthChecks();

    builder.Services.AddCors(o =>
    {
        // Wide-open in M1; tightened in M4 once the React SPA's origin is finalised.
        o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    });

    // M10/P0 — brute-force protection on credential endpoints. Fixed window:
    // 5 attempts per minute per client IP, queue disabled (excess => 429).
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("auth", httpContext =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit          = 5,
                    Window               = TimeSpan.FromMinutes(1),
                    QueueLimit           = 0,
                    AutoReplenishment    = true,
                }));
    });

    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseCors();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/",        () => Results.Text("KamsoraAPM Dashboard API\n  POST /api/v1/auth/login\n  GET  /api/v1/traces\n"));
    app.MapGet("/healthz", () => Results.Ok(new { status = "ok",    component = "kamsora-apm-dashboard-api" }));
    app.MapGet("/readyz",  () => Results.Ok(new { status = "ready", component = "kamsora-apm-dashboard-api" }));

    app.MapAuthEndpoints();
    app.MapTraceEndpoints();
    app.MapInsightsEndpoints();
    app.MapHostsEndpoints();
    app.MapAdminEndpoints();
    app.MapInvitesEndpoints();
    app.MapSelfServiceEndpoints();
    app.MapConsumersEndpoints();
    app.MapErrorsEndpoints();
    app.MapAlertingEndpoints();
    app.MapLogsEndpoints();
    app.MapMetricsEndpoints();
    app.MapServiceMapEndpoints();

    await app.RunAsync().ConfigureAwait(false);
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "KamsoraAPM Dashboard.Api terminated unexpectedly.");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

namespace KamsoraAPM.Dashboard.Api
{
    public partial class Program;
}
