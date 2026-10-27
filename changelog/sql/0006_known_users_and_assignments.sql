START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027055042_KnownUsersAndAssignments') THEN
    ALTER TABLE issues ADD assigned_user_full_name text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027055042_KnownUsersAndAssignments') THEN
    ALTER TABLE issues ADD assigned_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027055042_KnownUsersAndAssignments') THEN
    CREATE TABLE users (
        id uuid NOT NULL,
        issuer character varying(512) NOT NULL,
        subject character varying(256) NOT NULL,
        tenant_id character varying(64) NOT NULL,
        object_id character varying(64) NOT NULL,
        client_id character varying(64) NOT NULL,
        full_name character varying(256) NOT NULL,
        first_logged_in_at_utc timestamp with time zone NOT NULL,
        last_logged_in_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT "PK_users" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027055042_KnownUsersAndAssignments') THEN
    CREATE INDEX "IX_issues_assigned_user_id" ON issues (assigned_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027055042_KnownUsersAndAssignments') THEN
    CREATE INDEX ix_users_scope ON users (tenant_id, client_id, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027055042_KnownUsersAndAssignments') THEN
    CREATE UNIQUE INDEX ux_users_identity ON users (issuer, client_id, subject);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027055042_KnownUsersAndAssignments') THEN
    ALTER TABLE issues ADD CONSTRAINT "FK_issues_users_assigned_user_id" FOREIGN KEY (assigned_user_id) REFERENCES users (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027055042_KnownUsersAndAssignments') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261027055042_KnownUsersAndAssignments', '10.0.1');
    END IF;
END $EF$;
COMMIT;

