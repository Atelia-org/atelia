CREATE TABLE store_metadata(
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    schema_version INTEGER NOT NULL CHECK(schema_version = 3),
    store_instance_id TEXT NOT NULL,
    cell_count INTEGER NOT NULL CHECK(cell_count >= 0),
    row_view_count INTEGER NOT NULL CHECK(row_view_count >= 0),
    row_view_member_count INTEGER NOT NULL CHECK(row_view_member_count >= 0),
    fulfilled_view_count INTEGER NOT NULL CHECK(fulfilled_view_count >= 0)
) STRICT;

CREATE TABLE cell_artifact(
    cell_id TEXT PRIMARY KEY,
    recipe_digest TEXT NOT NULL,
    history_row_id TEXT NOT NULL,
    logical_column_id TEXT NOT NULL,
    definition_digest TEXT NOT NULL,
    outcome INTEGER NOT NULL CHECK(outcome IN (0, 1)),
    content TEXT NOT NULL,
    UNIQUE(recipe_digest, history_row_id, logical_column_id),
    UNIQUE(cell_id, logical_column_id, definition_digest)
) STRICT, WITHOUT ROWID;

CREATE TABLE row_view(
    row_result_id TEXT PRIMARY KEY,
    ref_id TEXT NOT NULL,
    timeline_id TEXT NOT NULL,
    history_row_id TEXT NOT NULL,
    row_descriptor_digest TEXT NOT NULL,
    recipe_digest TEXT NOT NULL,
    target_digest TEXT NOT NULL,
    previous_history_row_id TEXT,
    previous_row_result_id TEXT,
    bootstrap_completed INTEGER NOT NULL
        CHECK(bootstrap_completed IN (0, 1)),
    CHECK((previous_history_row_id IS NULL)
        = (previous_row_result_id IS NULL)),
    UNIQUE(ref_id, timeline_id, recipe_digest, history_row_id),
    UNIQUE(row_result_id, ref_id, timeline_id, recipe_digest,
        history_row_id, target_digest),
    UNIQUE(row_result_id, ref_id, timeline_id, recipe_digest,
        row_descriptor_digest),
    FOREIGN KEY(previous_row_result_id, ref_id, timeline_id, recipe_digest,
        previous_history_row_id, target_digest)
        REFERENCES row_view(row_result_id, ref_id, timeline_id, recipe_digest,
            history_row_id, target_digest)
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
    through_row_descriptor_digest TEXT NOT NULL,
    recipe_digest TEXT NOT NULL,
    row_result_id TEXT NOT NULL,
    PRIMARY KEY(ref_id, timeline_id, timeline_head_generation,
        through_row_descriptor_digest, recipe_digest),
    FOREIGN KEY(row_result_id, ref_id, timeline_id, recipe_digest,
        through_row_descriptor_digest)
        REFERENCES row_view(row_result_id, ref_id, timeline_id, recipe_digest,
            row_descriptor_digest)
) STRICT, WITHOUT ROWID;
