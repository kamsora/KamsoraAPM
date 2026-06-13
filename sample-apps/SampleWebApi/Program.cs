// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.
//
// Sample .NET 8 Web API instrumented with KamsoraAPM.Agent.
// Run with the Collector running locally on http://localhost:5080.

using KamsoraAPM.Agent.Extensions;

var builder = WebApplication.CreateBuilder(args);

// One-call wiring — the Agent subscribes an ActivityListener, owns its
// channel + background flusher, and exports spans to the Collector with
// every request. No middleware, no UseKamsoraApm() required.
builder.Services.AddKamsoraApm(builder.Configuration);

var app = builder.Build();

// Demo endpoints — each completes an HTTP server Activity, which the Agent
// converts to a Kamsora Span and exports.
app.MapGet("/", () => Results.Text(
    "KamsoraAPM sample Web API\n" +
    "  GET /weather  — synthetic weather forecast (200)\n" +
    "  GET /items/{id} — random latency + 4xx/5xx (mix of statuses)\n" +
    "  GET /boom     — throws an exception\n"));

string[] weatherSummaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

app.MapGet("/weather", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(i => new
    {
        date         = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(i)),
        temperatureC = Random.Shared.Next(-20, 35),
        summary      = weatherSummaries[Random.Shared.Next(weatherSummaries.Length)],
    }).ToArray();
    return Results.Ok(forecast);
});

app.MapGet("/items/{id:int}", async (int id, CancellationToken ct) =>
{
    // Simulate some work so the span has non-trivial duration.
    await Task.Delay(Random.Shared.Next(5, 60), ct).ConfigureAwait(false);
    if (id < 0)      return Results.BadRequest(new { error = "id must be non-negative" });
    if (id % 17 == 0) return Results.Problem("simulated 500", statusCode: 500);
    if (id % 11 == 0) return Results.NotFound(new { id });
    return Results.Ok(new { id, name = $"item-{id}", price = id * 1.99m });
});

app.MapGet("/boom", () =>
{
    throw new InvalidOperationException("Synthetic exception from /boom — the Agent should capture this on the server span.");
});

await app.RunAsync().ConfigureAwait(false);
