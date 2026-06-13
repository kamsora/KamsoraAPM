-- KamsoraAPM relational metadata - Transaction tables.
-- Operational state that mutates frequently: alert rules, dashboard layouts,
-- service registry, audit logs.

-- ---------------------------------------------------------------------------
-- tblapm_service_registry
-- Auto-populated when an Agent first reports a (tenant, service_name) pair.
-- Powers the "Infrastructure Topology" dashboard view.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.tblapm_service_registry
(
    serviceid                bigserial,
    sysservicetransid        text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    service_name             text        NOT NULL,
    service_namespace        text,
    sdk_language             text        DEFAULT 'dotnet'::text,
    sdk_version              text,
    runtime_version          text,
    first_seen_at            timestamp   DEFAULT CURRENT_TIMESTAMP,
    last_seen_at             timestamp   DEFAULT CURRENT_TIMESTAMP,
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_tblapm_service_registry        PRIMARY KEY (serviceid),
    CONSTRAINT uq_tblapm_service_registry_uuid   UNIQUE (sysservicetransid),
    CONSTRAINT uq_tblapm_service_registry_named  UNIQUE (systenantuuid, service_name),
    CONSTRAINT fk_tblapm_service_registry_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_tblapm_service_registry_tenant_lastseen
    ON public.tblapm_service_registry (systenantuuid, last_seen_at DESC);

-- ---------------------------------------------------------------------------
-- tblapm_alert_rules
-- A rule says: "evaluate condition X against ClickHouse every Y seconds.
-- If it holds for Z consecutive minutes, fire the alert to channel C."
-- Conditions are stored as a normalised JSON expression evaluated by the
-- rules engine (M5).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.tblapm_alert_rules
(
    ruleid                   bigserial,
    sysruletransid           text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    rule_name                text        NOT NULL,
    description              text,
    enabled                  boolean     DEFAULT true,
    -- Subject: which signal we evaluate
    signal_type              text        NOT NULL,   -- latency | error_rate | threadpool | gc | cpu | memory | custom
    service_filter           text,                   -- null = all services
    -- Condition
    operator                 text        NOT NULL,   -- gt | gte | lt | lte | eq
    threshold                double precision NOT NULL,
    -- Trigger window
    window_seconds           int         DEFAULT 60,
    for_seconds              int         DEFAULT 120,                 -- "sustained for" duration
    severity                 text        DEFAULT 'warning'::text,     -- info | warning | critical
    -- Targets
    channel_uuids            text[]      DEFAULT '{}'::text[],        -- references masteralert_channels.syschanneluuid
    -- Engine bookkeeping
    last_evaluated_at        timestamp,
    last_state               text        DEFAULT 'ok'::text,          -- ok | pending | firing
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_tblapm_alert_rules             PRIMARY KEY (ruleid),
    CONSTRAINT uq_tblapm_alert_rules_uuid        UNIQUE (sysruletransid),
    CONSTRAINT fk_tblapm_alert_rules_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE,
    CONSTRAINT ck_tblapm_alert_rules_signal
        CHECK (signal_type IN ('latency','error_rate','threadpool','gc','cpu','memory','custom')),
    CONSTRAINT ck_tblapm_alert_rules_operator
        CHECK (operator IN ('gt','gte','lt','lte','eq')),
    CONSTRAINT ck_tblapm_alert_rules_severity
        CHECK (severity IN ('info','warning','critical')),
    CONSTRAINT ck_tblapm_alert_rules_state
        CHECK (last_state IN ('ok','pending','firing'))
);

CREATE INDEX IF NOT EXISTS ix_tblapm_alert_rules_tenant_enabled
    ON public.tblapm_alert_rules (systenantuuid, enabled);

-- ---------------------------------------------------------------------------
-- tblapm_alert_firings
-- Append-only history of alert firings. Used by the dashboard "fired alerts"
-- view and by the engine to avoid double-firing.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.tblapm_alert_firings
(
    firingid                 bigserial,
    sysfiringtransid         text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    sysruletransid           text        NOT NULL,
    fired_at                 timestamp   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    resolved_at              timestamp,
    observed_value           double precision,
    severity                 text,
    payload_json             jsonb       NOT NULL DEFAULT '{}'::jsonb,  -- snapshot of context
    delivery_status_json     jsonb       NOT NULL DEFAULT '{}'::jsonb,  -- per-channel delivery outcomes
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_tblapm_alert_firings           PRIMARY KEY (firingid),
    CONSTRAINT uq_tblapm_alert_firings_uuid      UNIQUE (sysfiringtransid),
    CONSTRAINT fk_tblapm_alert_firings_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE,
    CONSTRAINT fk_tblapm_alert_firings_rule
        FOREIGN KEY (sysruletransid) REFERENCES public.tblapm_alert_rules (sysruletransid)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_tblapm_alert_firings_tenant_firedat
    ON public.tblapm_alert_firings (systenantuuid, fired_at DESC);

-- ---------------------------------------------------------------------------
-- tblapm_dashboard_layouts
-- Saved dashboard configurations. layout_json is interpreted by the React SPA.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.tblapm_dashboard_layouts
(
    layoutid                 bigserial,
    syslayouttransid         text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    layout_name              text        NOT NULL,
    description              text,
    layout_json              jsonb       NOT NULL DEFAULT '{}'::jsonb,
    is_default               boolean     DEFAULT false,
    owner_useruuid           text,
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_tblapm_dashboard_layouts       PRIMARY KEY (layoutid),
    CONSTRAINT uq_tblapm_dashboard_layouts_uuid  UNIQUE (syslayouttransid),
    CONSTRAINT fk_tblapm_dashboard_layouts_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_tblapm_dashboard_layouts_tenant
    ON public.tblapm_dashboard_layouts (systenantuuid);

-- ---------------------------------------------------------------------------
-- tblapm_audit_log
-- Admin actions: API keys created/revoked, alert rules changed, etc.
-- Append-only; never UPDATEd.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.tblapm_audit_log
(
    auditid                  bigserial,
    sysaudittransid          text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    actor_useruuid           text,
    action                   text        NOT NULL,                    -- e.g. 'apikey.created'
    target_kind              text,                                    -- e.g. 'masterapi_keys'
    target_uuid              text,
    before_json              jsonb,
    after_json               jsonb,
    client_ip                inet,
    user_agent               text,
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_tblapm_audit_log               PRIMARY KEY (auditid),
    CONSTRAINT uq_tblapm_audit_log_uuid          UNIQUE (sysaudittransid),
    CONSTRAINT fk_tblapm_audit_log_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_tblapm_audit_log_tenant_posted
    ON public.tblapm_audit_log (systenantuuid, posteddatetime DESC);
