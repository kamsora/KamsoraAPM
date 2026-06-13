#!/usr/bin/env bash
# bootstrap.sh - one-shot pre-flight for a fresh KamsoraAPM production host.
#
# What it does (idempotent):
#   1. Creates .env.production from the example if missing.
#   2. Generates strong random values for every secret left blank
#      (POSTGRES_PASSWORD, CLICKHOUSE_PASSWORD, JWT_SIGNING_KEY, SEED_ADMIN_PASSWORD).
#   3. Validates that KAMSORA_DOMAIN / KAMSORA_ACME_EMAIL / SEED_ADMIN_EMAIL
#      have been set to non-placeholder values.
#   4. Prints what to do next.
#
# It never overwrites a secret that already has a value, so you can safely
# re-run it after editing the file by hand.

set -euo pipefail

cd "$(dirname "$0")"

ENV_FILE=".env.production"
EXAMPLE=".env.production.example"

if [[ ! -f "$EXAMPLE" ]]; then
    echo "[bootstrap] ERROR: $EXAMPLE not found. Run this script from deploy/production/." >&2
    exit 1
fi

if [[ ! -f "$ENV_FILE" ]]; then
    cp "$EXAMPLE" "$ENV_FILE"
    chmod 600 "$ENV_FILE"
    echo "[bootstrap] Created $ENV_FILE from example."
fi

# --- Helpers ----------------------------------------------------------------

# Strong random secret (url-safe-ish base64, 32 bytes of entropy).
gen32() { openssl rand -base64 32 | tr -d '=\n' | tr '+/' '-_'; }
# Longer secret for the JWT signing key (64 bytes).
gen64() { openssl rand -base64 64 | tr -d '=\n' | tr '+/' '-_'; }
# Human-friendly admin password (still 18 base32 chars, ~90 bits of entropy).
genpw() { openssl rand -base64 18 | tr -d '=+/' | head -c 18; }

# Replace KEY= line if the value is currently empty.
fill_if_empty() {
    local key="$1"
    local val="$2"
    local current
    current=$(grep -E "^${key}=" "$ENV_FILE" | sed "s/^${key}=//")
    if [[ -z "${current}" ]]; then
        # macOS sed needs '' after -i; GNU sed does not. Handle both via tmpfile.
        awk -v k="$key" -v v="$val" 'BEGIN{FS=OFS="="} $1==k {print k"="v; next} {print}' \
            "$ENV_FILE" > "$ENV_FILE.tmp"
        mv "$ENV_FILE.tmp" "$ENV_FILE"
        echo "[bootstrap] Filled $key with a generated value."
    fi
}

# --- Generate secrets -------------------------------------------------------

fill_if_empty POSTGRES_PASSWORD     "$(gen32)"
fill_if_empty CLICKHOUSE_PASSWORD   "$(gen32)"
fill_if_empty JWT_SIGNING_KEY       "$(gen64)"
fill_if_empty SEED_ADMIN_PASSWORD   "$(genpw)"

chmod 600 "$ENV_FILE"

# --- Validate user-supplied values ------------------------------------------

needed_human=0
need_set() {
    local key="$1"
    local placeholder="$2"
    local val
    val=$(grep -E "^${key}=" "$ENV_FILE" | sed "s/^${key}=//")
    if [[ -z "$val" || "$val" == "$placeholder" ]]; then
        echo "[bootstrap] MISSING: $key (currently '$val'). Edit $ENV_FILE and set a real value."
        needed_human=1
    fi
}
need_set KAMSORA_DOMAIN     "apm.example.com"
need_set KAMSORA_ACME_EMAIL "ops@example.com"
need_set SEED_ADMIN_EMAIL   "admin@example.com"

echo
if (( needed_human )); then
    echo "[bootstrap] Edit $ENV_FILE, fill in the values flagged above, then re-run this script."
    exit 2
fi

# --- Print the admin password back to the operator --------------------------

ADMIN_PW=$(grep -E "^SEED_ADMIN_PASSWORD=" "$ENV_FILE" | sed "s/^SEED_ADMIN_PASSWORD=//")
ADMIN_EMAIL=$(grep -E "^SEED_ADMIN_EMAIL=" "$ENV_FILE" | sed "s/^SEED_ADMIN_EMAIL=//")
DOMAIN=$(grep -E "^KAMSORA_DOMAIN=" "$ENV_FILE" | sed "s/^KAMSORA_DOMAIN=//")

cat <<EOF
========================================================================
 KamsoraAPM bootstrap complete.

 Save these credentials - the admin password is shown ONLY here, NOT
 in the Dashboard.Api logs:

   Dashboard URL  : https://${DOMAIN}/
   Admin email    : ${ADMIN_EMAIL}
   Admin password : ${ADMIN_PW}

 Next step:
   docker compose --env-file .env.production -f docker-compose.production.yml up -d --build

 First boot is slow (~3 min) - Caddy is fetching a Let's Encrypt cert and
 the Dashboard.Api is seeding the first tenant. Watch progress with:
   docker compose -f docker-compose.production.yml logs -f
========================================================================
EOF
