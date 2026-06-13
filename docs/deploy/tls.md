# TLS for the Collector and Dashboard API

Both services run on Kestrel, so TLS is **pure configuration** — no code
changes or rebuilds. Two supported patterns:

## Pattern A — TLS terminated at a reverse proxy (recommended)

Run Caddy / nginx / Traefik in front and keep the services on loopback HTTP.

```
internet ──TLS──> nginx/caddy ──HTTP──> Dashboard.Api :5000
agents   ──TLS──> nginx (gRPC pass-through) ──h2c──> Collector :5080
```

nginx gRPC pass-through for the Collector:

```nginx
server {
    listen 443 ssl http2;
    server_name collector.example.com;
    ssl_certificate     /etc/ssl/collector.crt;
    ssl_certificate_key /etc/ssl/collector.key;

    location / {
        grpc_pass grpc://127.0.0.1:5080;
    }
}
```

Agent config then simply points at the TLS endpoint:

```json
"KamsoraApm": { "Endpoint": "https://collector.example.com" }
```

`GrpcChannel.ForAddress("https://…")` negotiates TLS natively — nothing else
to configure on the Agent side.

## Pattern B — Kestrel-native TLS (no proxy)

Give Kestrel a PFX (or PEM pair) via configuration:

```json
"Kestrel": {
  "Endpoints": {
    "Grpc": {
      "Url": "https://+:5443",
      "Protocols": "Http2",
      "Certificate": {
        "Path": "/etc/kamsora/tls/collector.pfx",
        "Password": "<pfx-password — supply via env var Kestrel__Endpoints__Grpc__Certificate__Password>"
      }
    },
    "Http": {
      "Url": "https://+:5444",
      "Protocols": "Http1",
      "Certificate": { "Path": "/etc/kamsora/tls/collector.pfx", "Password": "…" }
    }
  }
}
```

PEM pair instead of PFX:

```json
"Certificate": {
  "Path":    "/etc/kamsora/tls/fullchain.pem",
  "KeyPath": "/etc/kamsora/tls/privkey.pem"
}
```

The same `Kestrel:Endpoints` block works for the Dashboard API (HTTP/1.1
only). Remember to update the Vite proxy target / SPA API base URL to the
HTTPS address.

## What NOT to do

- Do not expose the Collector's plaintext :5080 to the internet — API keys
  travel in gRPC metadata and are readable on-path without TLS.
- Do not use long-lived self-signed certs agents are told to blindly trust;
  use a real CA (Let's Encrypt works for both patterns).
