-- Mirrors the CDP two-role model: cleanse_ddl owns the schema (POSTGRES_USER), cleanse is the
-- login the running service uses. Liquibase changeset 0002 grants it DML on the created objects.
CREATE ROLE cleanse LOGIN PASSWORD 'cleanse';

GRANT CONNECT ON DATABASE cleanse TO cleanse;

GRANT USAGE ON SCHEMA public TO cleanse;
