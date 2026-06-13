-- KamsoraAPM relational metadata - Master tables.
-- Follows the Kamsora PostgreSQL pattern: bigserial PK, UUID column,
-- audit columns (posteddatetime, postedby, updatedby, updateddatetime).

-- ---------------------------------------------------------------------------
-- mastertenants
-- The unit of isolation. Every piece of telemetry, every alert rule, every
-- dashboard layout is scoped to a tenant. systenantuuid is the value
-- propagated in gRPC metadata and ClickHouse partition keys.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.mastertenants
(
    tenantid                 bigserial,
    systenantuuid            text        DEFAULT uuid_generate_v4(),
    tenant_name              text        NOT NULL,
    tenant_slug              text        NOT NULL,
    plan_type                text        DEFAULT 'free'::text,        -- free | pro | enterprise
    data_retention_days      int         DEFAULT 14,
    max_spans_per_minute     int         DEFAULT 10000,
    max_metrics_per_minute   int         DEFAULT 60000,
    max_logs_per_minute      int         DEFAULT 30000,
    status                   text        DEFAULT 'active'::text,      -- active | suspended | deleted
    contact_email            text,
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_mastertenants                PRIMARY KEY (tenantid),
    CONSTRAINT uq_mastertenants_systenantuuid  UNIQUE (systenantuuid),
    CONSTRAINT uq_mastertenants_tenant_slug    UNIQUE (tenant_slug),
    CONSTRAINT ck_mastertenants_status         CHECK (status IN ('active','suspended','deleted'))
);

CREATE INDEX IF NOT EXISTS ix_mastertenants_status ON public.mastertenants (status);

-- ---------------------------------------------------------------------------
-- masterapi_keys
-- Credentials issued to a tenant. Agents and HostMonitor instances present
-- the secret in the `x-kamsora-api-key` gRPC metadata header. We store only
-- the BCrypt hash of the secret; the cleartext is revealed exactly once at
-- creation time.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.masterapi_keys
(
    apikeyid                 bigserial,
    sysapikeyuuid            text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    key_name                 text        NOT NULL,           -- human label
    key_prefix               text        NOT NULL,           -- first 8 chars of secret, for display
    key_hash                 text        NOT NULL,           -- bcrypt hash of full secret
    scopes                   text        DEFAULT 'ingest'::text, -- ingest | read | admin (comma-separated)
    expires_at               timestamp,
    revoked_at               timestamp,
    last_used_at             timestamp,
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_masterapi_keys               PRIMARY KEY (apikeyid),
    CONSTRAINT uq_masterapi_keys_uuid          UNIQUE (sysapikeyuuid),
    CONSTRAINT fk_masterapi_keys_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_masterapi_keys_tenant  ON public.masterapi_keys (systenantuuid);
CREATE INDEX IF NOT EXISTS ix_masterapi_keys_prefix  ON public.masterapi_keys (key_prefix);

-- ---------------------------------------------------------------------------
-- masterusers
-- Dashboard users. Auth lives outside this table (JWT issued by the
-- Dashboard.Api after Identity validation); this table stores per-tenant
-- role bindings.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.masterusers
(
    userid                   bigserial,
    sysuseruuid              text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    email                    text        NOT NULL,
    display_name             text,
    password_hash            text,                                    -- nullable when external IdP only
    role                     text        DEFAULT 'viewer'::text,      -- owner | admin | editor | viewer
    last_login_at            timestamp,
    status                   text        DEFAULT 'active'::text,      -- active | invited | disabled
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_masterusers                  PRIMARY KEY (userid),
    CONSTRAINT uq_masterusers_uuid             UNIQUE (sysuseruuid),
    CONSTRAINT uq_masterusers_tenant_email     UNIQUE (systenantuuid, email),
    CONSTRAINT fk_masterusers_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE,
    CONSTRAINT ck_masterusers_role             CHECK (role IN ('owner','admin','editor','viewer')),
    CONSTRAINT ck_masterusers_status           CHECK (status IN ('active','invited','disabled'))
);

CREATE INDEX IF NOT EXISTS ix_masterusers_tenant ON public.masterusers (systenantuuid);

-- ---------------------------------------------------------------------------
-- masteralert_channels
-- Where firing alerts go: webhooks, Slack, email, PagerDuty.
-- The payload field is a JSONB encoding of channel-specific config
-- (URL, headers, channel id, etc.). We do not store secrets in plaintext;
-- HMAC secrets and bearer tokens are referenced by a vault key.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.masteralert_channels
(
    channelid                bigserial,
    syschanneluuid           text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    channel_name             text        NOT NULL,
    channel_type             text        NOT NULL,                    -- webhook | slack | email | pagerduty
    config_json              jsonb       NOT NULL DEFAULT '{}'::jsonb,
    enabled                  boolean     DEFAULT true,
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_masteralert_channels         PRIMARY KEY (channelid),
    CONSTRAINT uq_masteralert_channels_uuid    UNIQUE (syschanneluuid),
    CONSTRAINT fk_masteralert_channels_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE,
    CONSTRAINT ck_masteralert_channels_type
        CHECK (channel_type IN ('webhook','slack','email','pagerduty'))
);

CREATE INDEX IF NOT EXISTS ix_masteralert_channels_tenant ON public.masteralert_channels (systenantuuid);
