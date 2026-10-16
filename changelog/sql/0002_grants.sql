-- Tables are created by the DDL user and are therefore owned by it. The running service connects
-- as the DML user, which needs these grants explicitly. Identity columns matter as much as the
-- tables: without USAGE on their sequences, inserting an audit entry fails with
-- "permission denied for sequence audit_entries_sequence_seq".
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO cleanse;

GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO cleanse;

-- Covers tables added by later migrations, so this changeset does not need repeating each time.
ALTER DEFAULT PRIVILEGES FOR ROLE cleanse_ddl IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO cleanse;

ALTER DEFAULT PRIVILEGES FOR ROLE cleanse_ddl IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO cleanse;
