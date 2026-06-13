-- KamsoraAPM relational metadata - PostgreSQL extensions.
-- Apply once per database. Idempotent.

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pgcrypto";
CREATE EXTENSION IF NOT EXISTS "pg_stat_statements";

-- Future-use: trigram indexes for search across alert rule names etc.
CREATE EXTENSION IF NOT EXISTS "pg_trgm";
