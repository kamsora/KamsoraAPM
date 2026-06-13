#!/usr/bin/env bash
# convert-pfx.sh — convert a Windows-style .pfx into cert.pem + key.pem
# for use with the BYO-cert Caddy deployment.
#
# Usage:
#   ./convert-pfx.sh /path/to/your.pfx
#   ./convert-pfx.sh /path/to/your.pfx --password "your-pfx-password"
#
# Output:
#   ./certs/cert.pem   (public cert + intermediate chain)
#   ./certs/key.pem    (private key, chmod 600)

set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "Usage: $0 <path-to-pfx> [--password <pfx-password>]" >&2
    exit 1
fi

PFX="$1"
shift

PASS_FLAG=""
if [[ $# -gt 0 ]]; then
    if [[ "$1" == "--password" && $# -ge 2 ]]; then
        PASS_FLAG="-passin pass:$2"
    fi
fi

if [[ ! -f "$PFX" ]]; then
    echo "PFX file not found: $PFX" >&2
    exit 1
fi

if ! command -v openssl >/dev/null 2>&1; then
    echo "openssl not found. Install it first (apt install openssl / brew install openssl)." >&2
    exit 1
fi

mkdir -p certs

echo "[convert-pfx] Extracting public cert + chain → certs/cert.pem ..."
# shellcheck disable=SC2086
openssl pkcs12 -in "$PFX" -out certs/cert.pem -clcerts -nokeys $PASS_FLAG

echo "[convert-pfx] Extracting private key → certs/key.pem ..."
# shellcheck disable=SC2086
openssl pkcs12 -in "$PFX" -out certs/key.pem -nocerts -nodes $PASS_FLAG

# Tighten permissions on the private key.
chmod 0600 certs/key.pem
chmod 0644 certs/cert.pem

echo
echo "[convert-pfx] Done. Certificate details:"
openssl x509 -in certs/cert.pem -noout -subject -issuer -dates

cat <<EOF

Next steps:
  1. Make sure KAMSORA_DOMAIN in .env.production is covered by the cert above
     (the 'subject' line should match or be a wildcard parent of your domain).
  2. Bring the stack up with both compose files layered:

       docker compose --env-file .env.production \\
           -f docker-compose.production.yml \\
           -f docker-compose.byo-cert.yml \\
           up -d --build

EOF
