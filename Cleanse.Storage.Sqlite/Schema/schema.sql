-- SQLite shape of the Cleanse schema. Deliberately not generated: it diverges from the Postgres
-- migration in ways that cannot be expressed by one model.
--
--   jsonb            -> TEXT holding JSON, queried with the JSON1 functions
--   timestamptz      -> TEXT in round-trip ISO-8601, which sorts correctly because every value
--                       this service writes is UTC with a fixed-width format
--   identity column  -> INTEGER PRIMARY KEY AUTOINCREMENT, which is the rowid and never reused
--   GIN index        -> no equivalent; context lookups scan, which is fine at dev/test volumes
--
-- Foreign keys are only enforced when PRAGMA foreign_keys = ON, which the connection factory
-- sets on every connection.

CREATE TABLE IF NOT EXISTS issues (
    id                          TEXT    NOT NULL PRIMARY KEY,
    group_id                    TEXT    NOT NULL,
    rule_id                     TEXT    NOT NULL,
    run_id                      TEXT    NOT NULL,
    status                      TEXT    NOT NULL CHECK (status IN ('active', 'inactive')),
    workflow_status             TEXT    NOT NULL CHECK (workflow_status IN ('todo', 'in-progress', 'suppressed', 'resolved', 'ignored', 'nonactionable')),
    assigned_to                 TEXT        NULL,
    notes                       TEXT        NULL,
    external_notes              TEXT        NULL,
    priority                    TEXT        NULL CHECK (priority IS NULL OR priority IN ('low', 'medium', 'high')),
    external_received_at_utc    TEXT        NULL,
    suppression_expires_at_utc  TEXT        NULL,
    context                     TEXT    NOT NULL DEFAULT '{}',
    created_at_utc              TEXT    NOT NULL,
    updated_at_utc              TEXT    NOT NULL,
    activated_at_utc            TEXT    NOT NULL,
    deactivated_at_utc          TEXT        NULL,
    version                     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_issues_sweep ON issues (group_id, status, run_id);

CREATE INDEX IF NOT EXISTS ix_issues_group_updated ON issues (group_id, updated_at_utc DESC, id);

CREATE INDEX IF NOT EXISTS ix_issues_group_rule ON issues (group_id, rule_id);

CREATE INDEX IF NOT EXISTS ix_issues_assigned ON issues (assigned_to) WHERE assigned_to IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_issues_suppression_expiry
    ON issues (suppression_expires_at_utc) WHERE workflow_status = 'suppressed';

CREATE TABLE IF NOT EXISTS rule_runs (
    run_id                 TEXT    NOT NULL PRIMARY KEY,
    group_id               TEXT    NOT NULL,
    state                  TEXT    NOT NULL CHECK (state IN ('running', 'completed', 'abandoned', 'failed', 'cancelled')),
    started_at_utc         TEXT    NOT NULL,
    finished_at_utc        TEXT        NULL,
    abandon_reason         TEXT        NULL,
    detections_processed   INTEGER NOT NULL DEFAULT 0,
    issues_created         INTEGER NOT NULL DEFAULT 0,
    issues_reactivated     INTEGER NOT NULL DEFAULT 0,
    issues_updated         INTEGER NOT NULL DEFAULT 0,
    issues_unchanged       INTEGER NOT NULL DEFAULT 0,
    issues_deactivated     INTEGER NOT NULL DEFAULT 0,
    current_stage          TEXT        NULL,
    records_processed      INTEGER NOT NULL DEFAULT 0,
    records_total          INTEGER NOT NULL DEFAULT 0,
    heartbeat_at_utc       TEXT        NULL,
    cancellation_requested INTEGER NOT NULL DEFAULT 0,
    failure_message        TEXT        NULL,
    failure_detail         TEXT        NULL,
    version                INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_rule_runs_group_started ON rule_runs (group_id, started_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_rule_runs_heartbeat
    ON rule_runs (heartbeat_at_utc) WHERE state = 'running';

-- Matches the Postgres guarantee: two schedulers cannot open a run for the same group.
CREATE UNIQUE INDEX IF NOT EXISTS ux_rule_runs_one_running_per_group
    ON rule_runs (group_id) WHERE state = 'running';

-- sequence is the primary key rather than id so SQLite assigns it monotonically. id stays unique,
-- and the pair gives the audit log the stable same-timestamp ordering the contract requires.
CREATE TABLE IF NOT EXISTS audit_entries (
    sequence            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    id                  TEXT    NOT NULL UNIQUE,
    issue_id            TEXT        NULL,
    run_id              TEXT        NULL,
    kind                TEXT    NOT NULL,
    actor_id            TEXT    NOT NULL,
    actor_display_name  TEXT    NOT NULL,
    actor_kind          TEXT    NOT NULL,
    at_utc              TEXT    NOT NULL,
    changes             TEXT    NOT NULL DEFAULT '[]'
);

CREATE INDEX IF NOT EXISTS ix_audit_entries_issue
    ON audit_entries (issue_id, at_utc DESC, sequence DESC) WHERE issue_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_audit_entries_run
    ON audit_entries (run_id, at_utc DESC, sequence DESC) WHERE run_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS issue_history (
    sequence                 INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    id                       TEXT    NOT NULL UNIQUE,
    issue_id                 TEXT    NOT NULL,
    action_type              TEXT    NOT NULL CHECK (action_type IN ('Comment', 'LetterSent', 'PhoneCall')),
    comments                 TEXT    NOT NULL,
    created_at_utc           TEXT    NOT NULL,
    created_by_id            TEXT    NOT NULL,
    created_by_display_name  TEXT    NOT NULL,
    created_by_kind          TEXT    NOT NULL,
    CONSTRAINT fk_issue_history_issue FOREIGN KEY (issue_id) REFERENCES issues (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_issue_history_issue
    ON issue_history (issue_id, created_at_utc DESC, sequence DESC);

CREATE TABLE IF NOT EXISTS import_jobs (
    id                     TEXT    NOT NULL PRIMARY KEY,
    group_id               TEXT    NOT NULL,
    file_name              TEXT    NOT NULL,
    blob_key               TEXT    NOT NULL,
    status                 TEXT    NOT NULL CHECK (status IN ('Pending', 'Processing', 'Completed', 'Failed', 'Abandoned')),
    total_rows             INTEGER NOT NULL DEFAULT 0,
    processed_rows         INTEGER NOT NULL DEFAULT 0,
    matched                INTEGER NOT NULL DEFAULT 0,
    unmatched              INTEGER NOT NULL DEFAULT 0,
    duplicates_collapsed   INTEGER NOT NULL DEFAULT 0,
    invalid                INTEGER NOT NULL DEFAULT 0,
    requested_by           TEXT    NOT NULL,
    created_at_utc         TEXT    NOT NULL,
    started_at_utc         TEXT        NULL,
    finished_at_utc        TEXT        NULL,
    heartbeat_at_utc       TEXT        NULL,
    failure_reason         TEXT        NULL,
    version                INTEGER NOT NULL
);

-- Matches the Postgres guarantee: a double-click cannot start two workers on one group.
CREATE UNIQUE INDEX IF NOT EXISTS ux_import_jobs_one_active_per_group
    ON import_jobs (group_id) WHERE status IN ('Pending', 'Processing');

CREATE INDEX IF NOT EXISTS ix_import_jobs_created ON import_jobs (created_at_utc DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_import_jobs_pending
    ON import_jobs (created_at_utc) WHERE status = 'Pending';

CREATE TABLE IF NOT EXISTS import_job_rejections (
    sequence        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    import_job_id   TEXT    NOT NULL,
    row_number      INTEGER NOT NULL,
    issue_id        TEXT        NULL,
    reason          TEXT    NOT NULL,
    detail          TEXT    NOT NULL,
    CONSTRAINT fk_import_job_rejections_job FOREIGN KEY (import_job_id) REFERENCES import_jobs (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_import_job_rejections_job
    ON import_job_rejections (import_job_id, row_number);

-- Deliberately outside the reset's blast radius. "When was this last wiped" cannot be answered
-- by a table the wipe empties.
CREATE TABLE IF NOT EXISTS data_resets (
    sequence   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    at_utc     TEXT    NOT NULL,
    scope      TEXT    NOT NULL,
    actor_id   TEXT    NOT NULL,
    issues     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_data_resets_at ON data_resets (at_utc DESC);
