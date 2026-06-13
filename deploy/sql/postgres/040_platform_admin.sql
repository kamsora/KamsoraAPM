-- KamsoraAPM M4.1 — platform admin role.
--
-- The seeded "first user" can administer ALL tenants (create / suspend / mint
-- keys). Per-tenant owners still only manage their own tenant. This column is
-- intentionally separate from `masterusers.role` (which is per-tenant) because
-- platform-admin authority spans tenants.
--
-- Idempotent: safe to re-run.

ALTER TABLE public.masterusers
    ADD COLUMN IF NOT EXISTS is_platform_admin boolean NOT NULL DEFAULT false;

-- Backfill: any existing owner of the seeded tenant becomes platform admin.
-- New tenant owners created post-M4.1 stay tenant-scoped (is_platform_admin=false).
UPDATE public.masterusers
   SET is_platform_admin = true,
       updatedby         = 'system:migration:040_platform_admin',
       updateddatetime   = CURRENT_TIMESTAMP
 WHERE role = 'owner'
   AND is_platform_admin = false
   AND (postedby = 'system:seeder' OR postedby IS NULL);

CREATE INDEX IF NOT EXISTS ix_masterusers_platform_admin
    ON public.masterusers (is_platform_admin)
 WHERE is_platform_admin = true;
