// Copyright 2026 Kamsora Technologies Pvt. Ltd. and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace KamsoraAPM.Storage.Extensions;

/// <summary>
/// Startup guard that refuses to run a <b>Production</b> host with the
/// well-known development secrets that ship in the repo's appsettings.json.
/// Self-hosters who deploy without replacing them would otherwise expose a
/// forgeable JWT signing key and default database credentials to the
/// internet. Failing fast at startup is the only reliable enforcement.
/// </summary>
public static class DevSecretsGuard
{
    /// <summary>Known development credential fragments shipped in the repo.</summary>
    private static readonly string[] KnownDevFragments =
    {
        "kamsora_dev_only_change_me",
        // base64("dev-only-please-replace-this-with-random-48-byte-key-now")
        "ZGV2LW9ubHktcGxlYXNlLXJlcGxhY2UtdGhpcy13aXRoLXJhbmRvbS00OC1ieXRlLWtleS1ub3c",
        "ChangeMe!2026",
    };

    /// <summary>
    /// Throw when the host runs in the Production environment and any bound
    /// configuration value still contains a known development secret.
    /// Call before <c>builder.Build()</c> completes its first request.
    /// </summary>
    /// <exception cref="InvalidOperationException">Dev secret detected in Production.</exception>
    public static void ThrowIfProductionWithDevSecrets(
        IHostEnvironment environment, IConfiguration configuration, params string[] sensitiveConfigKeys)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!environment.IsProduction()) return;

        var keysToCheck = new List<string>
        {
            "KamsoraApm:Postgres:ConnectionString",
            "KamsoraApm:ClickHouse:ConnectionString",
        };
        keysToCheck.AddRange(sensitiveConfigKeys);

        foreach (var key in keysToCheck)
        {
            var value = configuration[key];
            if (string.IsNullOrEmpty(value)) continue;

            foreach (var fragment in KnownDevFragments)
            {
                if (value.Contains(fragment, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"KamsoraAPM refuses to start: configuration key '{key}' still contains a " +
                        "development secret from the repository defaults while running in the " +
                        "Production environment. Replace it with a real secret (environment " +
                        "variable or secrets manager) before deploying. " +
                        "See docs/deploy/secrets.md.");
                }
            }
        }
    }
}
