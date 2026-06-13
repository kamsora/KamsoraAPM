-- KamsoraAPM M4.2 — self-service admin polish.
--
-- Adds:
--   * masterinvites          — tokenised user invites (replaces hand-crafted
--                              temp-password handoff for additional users).
--   * fn_api_post_tenant_status — toggle a tenant between active/suspended/deleted.
--   * fn_api_prune_audit_log — call from cron to enforce 90-day audit retention.
--
-- Idempotent: safe to re-run.

-- ---------------------------------------------------------------------------
-- masterinvites
-- An owner mints an invite for a new teammate's email + role. We store only
-- the SHA-256 hash of the token (cleartext is shown once at mint time and
-- handed to the invitee out-of-band — clipboard for M4.2; SMTP email later).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS public.masterinvites
(
    inviteid                 bigserial,
    sysinviteuuid            text        DEFAULT uuid_generate_v4(),
    systenantuuid            text        NOT NULL,
    email                    text        NOT NULL,
    role                     text        NOT NULL DEFAULT 'viewer',     -- owner | admin | editor | viewer
    token_hash               text        NOT NULL,                      -- SHA-256 hex of cleartext token
    token_prefix             text        NOT NULL,                      -- first 8 chars, for "Pending: kapi_a1b2…" display
    invited_by_useruuid      text,                                       -- masterusers.sysuseruuid of the issuer
    accepted_at              timestamp,
    accepted_useruuid        text,                                       -- masterusers.sysuseruuid of the resulting user
    revoked_at               timestamp,
    expires_at               timestamp   NOT NULL,
    posteddatetime           timestamp   DEFAULT CURRENT_TIMESTAMP,
    postedby                 text,
    updatedby                text,
    updateddatetime          timestamp,
    CONSTRAINT pk_masterinvites                PRIMARY KEY (inviteid),
    CONSTRAINT uq_masterinvites_uuid           UNIQUE (sysinviteuuid),
    CONSTRAINT fk_masterinvites_tenant
        FOREIGN KEY (systenantuuid) REFERENCES public.mastertenants (systenantuuid)
        ON DELETE CASCADE,
    CONSTRAINT ck_masterinvites_role
        CHECK (role IN ('owner','admin','editor','viewer'))
);

-- Lookup the token by its prefix (cheap O(1) seek), then verify the full hash
-- in C# — avoids storing or transmitting the cleartext to the DB.
CREATE INDEX IF NOT EXISTS ix_masterinvites_prefix ON public.masterinvites (token_prefix);

-- Owner list query: open invites for "my tenant".
CREATE INDEX IF NOT EXISTS ix_masterinvites_tenant_open
    ON public.masterinvites (systenantuuid)
 WHERE accepted_at IS NULL AND revoked_at IS NULL;

-- ---------------------------------------------------------------------------
-- fn_api_post_tenant_status
-- Owner of the platform admin role flips a tenant between active / suspended
-- / deleted. Deleted is soft (ClickHouse data retained for compliance per the
-- tenant's data_retention_days); the resolver + login query already filter on
-- status='active' so this single UPDATE blocks both ingest and dashboard
-- access. Note: PostgresTenantResolver has a 5-minute positive cache, so
-- suspended tenants may keep ingesting for up to 5 minutes.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.fn_api_post_tenant_status(
    _systenantuuid text,
    _new_status    text,
    _updatedby     text)
    RETURNS text
    LANGUAGE 'plpgsql'
    COST 100
    VOLATILE PARALLEL UNSAFE
AS $BODY$
BEGIN
    IF _new_status NOT IN ('active','suspended','deleted') THEN
        RAISE EXCEPTION 'invalid tenant status: %', _new_status;
    END IF;

    UPDATE public.mastertenants
       SET status          = _new_status,
           updatedby       = _updatedby,
           updateddatetime = CURRENT_TIMESTAMP
     WHERE systenantuuid = _systenantuuid
       AND status <> 'deleted';   -- deleted is terminal; cannot resurrect via this fn

    RETURN _systenantuuid;
END;
$BODY$;

-- ---------------------------------------------------------------------------
-- fn_api_prune_audit_log
-- Deletes tblapm_audit_log rows older than _retention_days (default 90).
-- Returns the number of rows pruned. Wire to cron in production:
--   0 4 * * *  psql -c "SELECT public.fn_api_prune_audit_log(90)"
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.fn_api_prune_audit_log(_retention_days int)
    RETURNS bigint
    LANGUAGE 'plpgsql'
    COST 100
    VOLATILE PARALLEL UNSAFE
AS $BODY$
DECLARE
    _cutoff  timestamp;
    _deleted bigint;
BEGIN
    _cutoff := CURRENT_TIMESTAMP - (COALESCE(_retention_days, 90) || ' days')::interval;

    DELETE FROM public.tblapm_audit_log
     WHERE posteddatetime < _cutoff;

    GET DIAGNOSTICS _deleted = ROW_COUNT;
    RETURN _deleted;
END;
$BODY$;
