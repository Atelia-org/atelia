CREATE TABLE store_metadata(
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    schema_version INTEGER NOT NULL CHECK(schema_version = 5),
    store_instance_id TEXT NOT NULL,
    cell_count INTEGER NOT NULL CHECK(cell_count >= 0),
    row_view_count INTEGER NOT NULL CHECK(row_view_count >= 0),
    row_view_member_count INTEGER NOT NULL CHECK(row_view_member_count >= 0),
    fulfilled_view_count INTEGER NOT NULL CHECK(fulfilled_view_count >= 0)
) STRICT;

CREATE TABLE row_work(
    work_id TEXT PRIMARY KEY,
    ref_id TEXT NOT NULL,
    timeline_id TEXT NOT NULL,
    root_recipe_digest TEXT NOT NULL,
    history_row_id TEXT NOT NULL,
    previous_history_row_id TEXT,
    previous_row_result_id TEXT,
    producer_target BLOB NOT NULL,
    canonical BLOB NOT NULL,
    UNIQUE(ref_id, timeline_id, root_recipe_digest, history_row_id),
    CHECK((previous_history_row_id IS NULL) = (previous_row_result_id IS NULL))
) STRICT, WITHOUT ROWID;

CREATE TABLE row_work_member(
    work_id TEXT NOT NULL,
    column_ordinal INTEGER NOT NULL CHECK(column_ordinal >= 0),
    logical_column_id TEXT NOT NULL,
    definition_digest TEXT NOT NULL,
    reused_cell_id TEXT,
    PRIMARY KEY(work_id, column_ordinal),
    UNIQUE(work_id, logical_column_id),
    FOREIGN KEY(work_id) REFERENCES row_work(work_id)
) STRICT, WITHOUT ROWID;

CREATE TABLE cell_artifact(
    cell_id TEXT PRIMARY KEY,
    recipe_digest TEXT NOT NULL,
    history_row_id TEXT NOT NULL,
    work_id TEXT,
    logical_column_id TEXT NOT NULL,
    definition_digest TEXT NOT NULL,
    outcome INTEGER NOT NULL CHECK(outcome IN (0, 1)),
    content TEXT NOT NULL,
    UNIQUE(recipe_digest, history_row_id, logical_column_id),
    UNIQUE(work_id, logical_column_id),
    UNIQUE(cell_id, logical_column_id, definition_digest),
    FOREIGN KEY(work_id) REFERENCES row_work(work_id)
) STRICT, WITHOUT ROWID;

CREATE TABLE row_view(
    row_result_id TEXT PRIMARY KEY,
    ref_id TEXT NOT NULL,
    timeline_id TEXT NOT NULL,
    history_row_id TEXT NOT NULL,
    recipe_digest TEXT NOT NULL,
    target_digest TEXT NOT NULL,
    work_id TEXT,
    previous_history_row_id TEXT,
    previous_row_result_id TEXT,
    bootstrap_completed INTEGER NOT NULL CHECK(bootstrap_completed IN (0, 1)),
    CHECK((previous_history_row_id IS NULL) = (previous_row_result_id IS NULL)),
    UNIQUE(ref_id, timeline_id, recipe_digest, history_row_id),
    UNIQUE(row_result_id, ref_id, timeline_id, recipe_digest, history_row_id, target_digest),
    UNIQUE(row_result_id, ref_id, timeline_id, recipe_digest, history_row_id),
    FOREIGN KEY(work_id) REFERENCES row_work(work_id),
    FOREIGN KEY(previous_row_result_id, ref_id, timeline_id, recipe_digest, previous_history_row_id)
        REFERENCES row_view(row_result_id, ref_id, timeline_id, recipe_digest, history_row_id)
) STRICT, WITHOUT ROWID;

CREATE TABLE row_view_member(
    row_result_id TEXT NOT NULL,
    column_ordinal INTEGER NOT NULL CHECK(column_ordinal >= 0),
    logical_column_id TEXT NOT NULL,
    definition_digest TEXT NOT NULL,
    cell_id TEXT NOT NULL,
    PRIMARY KEY(row_result_id, column_ordinal),
    UNIQUE(row_result_id, logical_column_id),
    FOREIGN KEY(row_result_id) REFERENCES row_view(row_result_id),
    FOREIGN KEY(cell_id, logical_column_id, definition_digest)
        REFERENCES cell_artifact(cell_id, logical_column_id, definition_digest)
) STRICT, WITHOUT ROWID;

CREATE TABLE fulfilled_view_ref(
    ref_id TEXT NOT NULL,
    timeline_id TEXT NOT NULL,
    timeline_head_generation INTEGER NOT NULL CHECK(timeline_head_generation >= 0),
    through_history_row_id TEXT NOT NULL,
    recipe_digest TEXT NOT NULL,
    row_result_id TEXT NOT NULL,
    PRIMARY KEY(ref_id, timeline_id, timeline_head_generation, through_history_row_id, recipe_digest),
    FOREIGN KEY(row_result_id, ref_id, timeline_id, recipe_digest, through_history_row_id)
        REFERENCES row_view(row_result_id, ref_id, timeline_id, recipe_digest, history_row_id)
) STRICT, WITHOUT ROWID;
