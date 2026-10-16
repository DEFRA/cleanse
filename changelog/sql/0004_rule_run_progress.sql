START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    ALTER TABLE rule_runs DROP CONSTRAINT ck_rule_runs_state;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    ALTER TABLE rule_runs ADD cancellation_requested boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    ALTER TABLE rule_runs ADD current_stage text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    ALTER TABLE rule_runs ADD failure_detail text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    ALTER TABLE rule_runs ADD failure_message text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    ALTER TABLE rule_runs ADD heartbeat_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    ALTER TABLE rule_runs ADD records_processed integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    ALTER TABLE rule_runs ADD records_total integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    CREATE INDEX ix_rule_runs_heartbeat ON rule_runs (heartbeat_at_utc) WHERE state = 'running';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    ALTER TABLE rule_runs ADD CONSTRAINT ck_rule_runs_state CHECK (state IN ('running', 'completed', 'abandoned', 'failed', 'cancelled'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015113029_RuleRunProgress') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261015113029_RuleRunProgress', '10.0.1');
    END IF;
END $EF$;
COMMIT;

