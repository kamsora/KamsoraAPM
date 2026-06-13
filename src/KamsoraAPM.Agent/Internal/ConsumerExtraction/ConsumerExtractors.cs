// Copyright 2026 Kamsora and KamsoraAPM contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using KamsoraAPM.Agent.Options;
using Microsoft.AspNetCore.Http;

namespace KamsoraAPM.Agent.Internal.ConsumerExtraction;

/// <summary>No-op extractor — every span gets an empty consumer id.</summary>
internal sealed class NullConsumerExtractor : IConsumerExtractor
{
    public string? Extract(HttpRequest request) => null;
}

/// <summary>
/// Pulls the consumer id from a claim on the request's authenticated user.
/// Reads <see cref="HttpContext.User"/> first (cheap, no parsing). If that
/// is anonymous, falls back to manually parsing the Bearer token from the
/// Authorization header — useful when the Agent is wired in BEFORE
/// <c>UseAuthentication()</c> runs.
/// </summary>
internal sealed class JwtClaimConsumerExtractor : IConsumerExtractor
{
    private readonly string _claimName;
    private readonly bool   _fallbackToClientIp;

    public JwtClaimConsumerExtractor(ConsumerExtractorOptions options)
    {
        _claimName = string.IsNullOrWhiteSpace(options.ClaimName) ? "sub" : options.ClaimName;
        _fallbackToClientIp = options.FallbackToClientIp;
    }

    public string? Extract(HttpRequest request)
    {
        try
        {
            // Fast path: the authentication middleware has already populated User.
            var user = request.HttpContext.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                var claim = user.FindFirst(_claimName);
                if (claim is not null && !string.IsNullOrWhiteSpace(claim.Value))
                    return claim.Value;
            }

            // Fallback: read the claim straight out of the bearer JWT's payload.
            // No signature validation — instrumentation must never gate on auth.
            // We avoid System.IdentityModel.Tokens.Jwt to keep the Agent NuGet
            // lean; the payload is just base64url-encoded JSON.
            var auth = request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = auth["Bearer ".Length..].Trim();
                var claim = ReadJwtClaim(token, _claimName);
                if (!string.IsNullOrWhiteSpace(claim)) return claim;
            }
        }
        catch
        {
            // Swallow — instrumentation must never throw on the hot path.
        }

        if (_fallbackToClientIp)
            return ClientIpConsumerExtractor.ExtractIp(request);
        return null;
    }

    /// <summary>
    /// Extracts a single string claim from a JWT's payload without validating
    /// the signature. Returns null on any parse error.
    /// </summary>
    private static string? ReadJwtClaim(string token, string claimName)
    {
        try
        {
            var dot1 = token.IndexOf('.');
            if (dot1 < 0) return null;
            var dot2 = token.IndexOf('.', dot1 + 1);
            if (dot2 < 0) return null;

            var payloadB64 = token.AsSpan(dot1 + 1, dot2 - dot1 - 1);
            var json = DecodeBase64Url(payloadB64);
            if (json is null) return null;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(claimName, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _                    => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? DecodeBase64Url(ReadOnlySpan<char> b64url)
    {
        // base64url → base64: + ←→ -, / ←→ _, padding stripped.
        var padded = new char[b64url.Length + 3];
        var n = 0;
        foreach (var c in b64url)
        {
            padded[n++] = c switch { '-' => '+', '_' => '/', _ => c };
        }
        while (n % 4 != 0) padded[n++] = '=';

        try
        {
            var bytes = Convert.FromBase64CharArray(padded, 0, n);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Reads a single request header (default <c>X-API-Consumer</c>) as the
/// consumer id. Optionally falls back to the client IP when the header
/// is absent.
/// </summary>
internal sealed class HeaderConsumerExtractor : IConsumerExtractor
{
    private readonly string _headerName;
    private readonly bool   _fallbackToClientIp;

    public HeaderConsumerExtractor(ConsumerExtractorOptions options)
    {
        _headerName = string.IsNullOrWhiteSpace(options.HeaderName) ? "X-API-Consumer" : options.HeaderName;
        _fallbackToClientIp = options.FallbackToClientIp;
    }

    public string? Extract(HttpRequest request)
    {
        try
        {
            if (request.Headers.TryGetValue(_headerName, out var values))
            {
                var v = values.ToString();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { /* never throw on the hot path */ }

        if (_fallbackToClientIp)
            return ClientIpConsumerExtractor.ExtractIp(request);
        return null;
    }
}

/// <summary>
/// Uses the client IP as the consumer id. Honors <c>X-Forwarded-For</c>
/// when the app sits behind a reverse proxy.
/// </summary>
internal sealed class ClientIpConsumerExtractor : IConsumerExtractor
{
    public string? Extract(HttpRequest request) => ExtractIp(request);

    internal static string? ExtractIp(HttpRequest request)
    {
        try
        {
            if (request.Headers.TryGetValue("X-Forwarded-For", out var xff))
            {
                var first = xff.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (first.Length > 0 && !string.IsNullOrWhiteSpace(first[0])) return first[0];
            }
            return request.HttpContext.Connection.RemoteIpAddress?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
