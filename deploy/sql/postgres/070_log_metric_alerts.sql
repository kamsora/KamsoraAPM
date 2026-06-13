-- KamsoraAPM - log + metric alert rules.
--
-- Extends the M7.1 alert engine beyond span signals:
--   * log_count   - number of log records at/above a severity floor in the
--                   window. signal_param = severity floor name (default ERROR).
--   * metric_avg  - avg of a metric's scalar value in the window.
--                   signal_param = metric name (required).
--   * metric_max  - max of a metric's scalar value in the window.
--                   signal_param = metric name (required).
--
-- Adds tblapm_alert_rules.signal_param (generic per-signal parameter) and
-- replaces fn_api_post_tblapm_alert_rules with a 13-arg version that accepts
-- it. Idempotent: safe to re-run.

ALTER TABLE public.tblapm_alert_rules
    ADD COLUMN IF NOT EXISTS signal_param text;

-- Expand the allowed signal list.
ALTER TABLE public.tblapm_alert_rules
    DROP CONSTRAINT IF EXISTS ck_tblapm_alert_rules_signal;

ALTER TABLE public.tblapm_alert_rules
    ADD CONSTRAINT ck_tblapm_alert_rules_signal
    CHECK (signal_type IN (
        'latency_p50','latency_p90','latency_p99',
        'error_rate','request_volume',
        'log_count','metric_avg','metric_max',
        -- legacy values kept for backwards compatibility.
        'latency','threadpool','gc','cpu','memory','custom'
    ));

-- Drop the old 12-arg insert function so callers can't hit the stale overload,
-- then create the 13-arg replacement.
DROP FUNCTION IF EXISTS public.fn_api_post_tblapm_alert_rules(
    text, text, text, text, text, text, double precision, int, int, text, text[], text);

CREATE OR REPLACE FUNCTION public.fn_api_post_tblapm_alert_rules(
    _systenantuuid    text,
    _rule_name        text,
    _description      text,
    _signal_type      text,
    _signal_param     text,
    _service_filter   text,
    _operator         text,
    _threshold        double precision,
    _window_seconds   int,
    _for_seconds      int,
    _severity         text,
    _channel_uuids    text[],
    _postedby         text)
    RETURNS text
    LANGUAGE 'plpgsql'
    COST 100
    VOLATILE PARALLEL UNSAFE
AS $BODY$
DECLARE
    _sysRuleTransId text;
BEGIN
    INSERT INTO public.tblapm_alert_rules(
        systenantuuid, rule_name, description, signal_type, signal_param,
        service_filter, operator, threshold, window_seconds, for_seconds,
        severity, channel_uuids, posteddatetime, postedby
    )
    SELECT
        _systenantuuid, _rule_name, _description, _signal_type, _signal_param,
        _service_filter, _operator, _threshold,
        COALESCE(_window_seconds, 60),
        COALESCE(_for_seconds, 120),
        COALESCE(_severity, 'warning'),
        COALESCE(_channel_uuids, '{}'::text[]),
        CURRENT_TIMESTAMP, _postedby
    RETURNING sysruletransid INTO _sysRuleTransId;

    RETURN _sysRuleTransId;
END;
$BODY$;
