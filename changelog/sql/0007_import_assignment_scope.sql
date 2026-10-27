START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027063740_ImportAssignmentScope') THEN
    ALTER TABLE import_jobs ADD client_id text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027063740_ImportAssignmentScope') THEN
    ALTER TABLE import_jobs ADD tenant_id text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027063740_ImportAssignmentScope') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261027063740_ImportAssignmentScope', '10.0.1');
    END IF;
END $EF$;
COMMIT;

