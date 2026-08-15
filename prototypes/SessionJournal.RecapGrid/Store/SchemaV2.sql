CREATE TABLE store_metadata(
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    schema_version INTEGER NOT NULL CHECK(schema_version = 2),
    store_instance_id TEXT NOT NULL,
    cell_count INTEGER NOT NULL CHECK(cell_count >= 0),
    row_view_count INTEGER NOT NULL CHECK(row_view_count >= 0),
    row_view_member_count INTEGER NOT NULL CHECK(row_view_member_count >= 0),
    fulfilled_view_count INTEGER NOT NULL CHECK(fulfilled_view_count >= 0)
) STRICT;

CREATE TABLE cell_artifact(
    cell_digest TEXT PRIMARY KEY,
    evaluation_key_digest TEXT NOT NULL UNIQUE,
    history_segment_digest TEXT NOT NULL,
    logical_column_id TEXT NOT NULL,
    definition_digest TEXT NOT NULL,
    content_digest TEXT NOT NULL,
    canonical BLOB NOT NULL,
    UNIQUE(cell_digest, logical_column_id, definition_digest)
) STRICT, WITHOUT ROWID;

CREATE TABLE row_view(
    view_digest TEXT PRIMARY KEY,
    ref_id TEXT NOT NULL,
    timeline_id TEXT NOT NULL,
    history_row_id TEXT NOT NULL,
    row_descriptor_digest TEXT NOT NULL,
    recipe_digest TEXT NOT NULL,
    target_digest TEXT NOT NULL,
    previous_history_row_id TEXT,
    previous_view_digest TEXT,
    bootstrap_completed INTEGER NOT NULL
        CHECK(bootstrap_completed IN (0, 1)),
    canonical BLOB NOT NULL,
    CHECK((previous_history_row_id IS NULL)
        = (previous_view_digest IS NULL)),
    UNIQUE(ref_id, timeline_id, recipe_digest, history_row_id),
    UNIQUE(view_digest, ref_id, timeline_id, recipe_digest,
        history_row_id, target_digest),
    UNIQUE(view_digest, ref_id, timeline_id, recipe_digest,
        row_descriptor_digest),
    FOREIGN KEY(previous_view_digest, ref_id, timeline_id, recipe_digest,
        previous_history_row_id, target_digest)
        REFERENCES row_view(view_digest, ref_id, timeline_id, recipe_digest,
            history_row_id, target_digest)
) STRICT, WITHOUT ROWID;

CREATE TABLE row_view_member(
    view_digest TEXT NOT NULL,
    column_ordinal INTEGER NOT NULL CHECK(column_ordinal >= 0),
    logical_column_id TEXT NOT NULL,
    definition_digest TEXT NOT NULL,
    cell_digest TEXT NOT NULL,
    PRIMARY KEY(view_digest, column_ordinal),
    UNIQUE(view_digest, logical_column_id),
    FOREIGN KEY(view_digest) REFERENCES row_view(view_digest),
    FOREIGN KEY(cell_digest, logical_column_id, definition_digest)
        REFERENCES cell_artifact(cell_digest, logical_column_id, definition_digest)
) STRICT, WITHOUT ROWID;

CREATE TABLE fulfilled_view_ref(
    ref_id TEXT NOT NULL,
    timeline_id TEXT NOT NULL,
    timeline_head_generation INTEGER NOT NULL CHECK(timeline_head_generation >= 0),
    through_row_descriptor_digest TEXT NOT NULL,
    recipe_digest TEXT NOT NULL,
    key_canonical BLOB NOT NULL,
    view_digest TEXT NOT NULL,
    PRIMARY KEY(ref_id, timeline_id, timeline_head_generation,
        through_row_descriptor_digest, recipe_digest),
    FOREIGN KEY(view_digest, ref_id, timeline_id, recipe_digest,
        through_row_descriptor_digest)
        REFERENCES row_view(view_digest, ref_id, timeline_id, recipe_digest,
            row_descriptor_digest)
) STRICT, WITHOUT ROWID;
