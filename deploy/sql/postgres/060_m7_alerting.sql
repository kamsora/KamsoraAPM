-- KamsoraAPM M7.1 - alerting engine schema additions.
--
-- The base alert tables (tblapm_alert_rules, tblapm_alert_firings,
-- masteralert_channels) already exist from the M1 schema. This migration
-- adds the columns + constraints the M7.1 engine needs:
--
--   * tblapm_alert_rules.last_pending_at - when did this rule enter
--     'pending' state? Used to check whether for_seconds has elapsed and
--     it's time to fire.
--   * tblapm_alert_rules.last_value - last observed signal value, so
--     dashboards can show "current 712ms vs threshold 500ms".
--   * Expand signal_type CHECK to cover the granular percentile names
--     the M7.1 UI emits (latency_p50/p90/p99 + request_volume).
--   * Add a tblapm_inapp_notifications table for the dashboard banner
--     stream - channels of type 'inapp' write here instead of doing
--     HTTP egress.
--
-- Idempotent: safe to re-run.

ALTER TABLE public.tblapm_alert_rules
    ADD COLUMN IF NOT EXISTS last_pending_at timestamp,
    ADD COLUMN IF NOT EXISTS last_value      double precision;

-- Drop the old narrow CHECK if it exists, then add the expanded one.
ALTER TABLE public.tblapm_alert_rules
    DROP CONSTRAINT IF EXISTS ck_tblapm_alert_rules_signal;

ALTER TABLE public.tblapm_alert_rules
    ADD CONSTRAINT ck_tblapm_alert_rules_signal
    CHECK (signal_type IN (
        'latency_p50','latency_p90','latency_p99',
        'error_rate','request_volume',
        -- legacy values kept for backwards compatibility with any rows the
        -- early M1 schema might have allowed.
        'latency','threadpool','gc','cpu','memory','custom'
    ));

-- ---------------------------------------------------------------------------
-- masteralert_channels: expand channel_type CHECK so 'inapp' is allowed.
-- inapp = "show in the dashboard's notification banner"; no external HTTP egress.
-- ---------------------------------------------------------------------------
ALTER TABLE public.masteralert_channels
    DROP CONSTRAINT IF EXISTS ck_masteralert_channels_type;

ALTER TABLE public.masteralert_channels
    ADD CONSTRAINT ck_masteralert_channels_type
    CHECK (channel_type IN ('webhook','slack','email','pagerduty','inapp'));

-- ---------------------------------------------------------------------------
-- tblapm_inapp_notifications
-- Channel handlers of type 'inapp' write a row here on every firing. The
-- dashboard polls /v1/alerts/notifications/recent and renders a banner on top.
-- Append-only; we keep a 7-day TTL via fn_api_prune_inapp_notifications.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.tblapm_inapp_notifications
(
    notificationid           bigserial,
    sysnotificationtransid   text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    sysfiringtransid         text        NOT NULL,
    sysruletransid           text        NOT NULL,
    title                    text        NOT NULL,    -- e.g. "p99 latency > 500ms"
    body                     text,                    -- human-readable detail
    severity                 text        NOT NULL DEFAULT 'warning',
    observed_value           double precision,
    threshold                double precision,
    rule_signal              text,
    acknowledged_at          timestamp,
    acknowledged_useruuid    text,
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    CONSTRAINT pk_tblapm_inapp_notifications        PRIMARY KEY (notificationid),
    CONSTRAINT uq_tblapm_inapp_notifications_uuid   UNIQUE (sysnotificationtransid),
    CONSTRAINT fk_tblapm_inapp_notifications_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE,
    CONSTRAINT ck_tblapm_inapp_notifications_severity
        CHECK (severity IN ('info','warning','critical'))
);

CREATE INDEX IF NOT EXISTS ix_tblapm_inapp_notifications_tenant_active
    ON public.tblapm_inapp_notifications (systenantuuid, posteddatetime DESC)
 WHERE acknowledged_at IS NULL;

-- ---------------------------------------------------------------------------
-- fn_api_prune_inapp_notifications
-- Cron: 0 5 * * *  psql -c "SELECT public.fn_api_prune_inapp_notifications(7)"
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.fn_api_prune_inapp_notifications(_retention_days int)
    RETURNS bigint
    LANGUAGE 'plpgsql'
    COST 100
    VOLATILE PARALLEL UNSAFE
AS $BODY$
DECLARE
    _cutoff  timestamp;
    _deleted bigint;
BEGIN
    _cutoff := CURRENT_TIMESTAMP - (COALESCE(_retention_days, 7) || ' days')::interval;
    DELETE FROM public.tblapm_inapp_notifications WHERE posteddatetime < _cutoff;
    GET DIAGNOSTICS _deleted = ROW_COUNT;
    RETURN _deleted;
END;
$BODY$;
