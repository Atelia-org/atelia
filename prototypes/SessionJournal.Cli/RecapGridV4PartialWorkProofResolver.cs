using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Microsoft.Data.Sqlite;

namespace Atelia.SessionJournal.Cli;

/// <summary>
/// Reads only stopped V4 durable facts to prove each partial cell's scope,
/// actual root target, and exact predecessor. Store validates and commits the
/// resulting RowWork; this resolver never calls a provider or changes state.
/// </summary>
internal static class RecapGridV4PartialWorkProofResolver {
    internal static IReadOnlyList<RowWork> Resolve(string repositoryPath) {
        string database = Path.Combine(repositoryPath, "derived", "recap-grid",
            "v1", "grid.sqlite");
        if (!File.Exists(database)) {
            return [];
        }
        using SqliteConnection connection = OpenReadOnly(database);
        if (ReadSchemaVersion(connection) != 4) {
            return [];
        }
        IReadOnlyList<V4Cell> partial = ReadPartialCells(connection);
        if (partial.Count == 0) {
            return [];
        }
        IReadOnlyList<V4Row> rows = ReadRows(connection);
        return partial.GroupBy(static cell => (cell.RecipeDigest,
                cell.HistoryRowId))
            .Select(group => ResolveGroup(repositoryPath, rows,
                group.Key.RecipeDigest, group.Key.HistoryRowId,
                group.ToArray()))
            .ToArray();
    }

    private static RowWork ResolveGroup(
        string repositoryPath,
        IReadOnlyList<V4Row> rows,
        string rootRecipeDigest,
        string historyRowId,
        IReadOnlyList<V4Cell> cells
    ) {
        V4Scope[] scopes = rows.Select(static row => new V4Scope(
                row.RefId, row.TimelineId))
            .Distinct()
            .ToArray();
        RowWork[] candidates = scopes.Select(scope => TryResolveScope(
                repositoryPath, scope, rows, rootRecipeDigest, historyRowId,
                cells))
            .Where(static value => value is not null)
            .Cast<RowWork>()
            .ToArray();
        return candidates.Length switch {
            1 => candidates[0],
            0 => throw new InvalidDataException(
                "V4 partial cell has no provable Control/Timeline scope and exact prior."),
            _ => throw new InvalidDataException(
                "V4 partial cell belongs to multiple Control/Timeline scopes; choose no scope by guess.")
        };
    }

    private static RowWork? TryResolveScope(
        string repositoryPath,
        V4Scope scope,
        IReadOnlyList<V4Row> rows,
        string rootRecipeDigest,
        string historyRowId,
        IReadOnlyList<V4Cell> cells
    ) {
        RefId refId = ParseRef(scope.RefId);
        RecapGridControlReaderOpenResult opened =
            RecapGridControlFactory.OpenReader(repositoryPath, refId);
        if (opened is not RecapGridControlReaderOpenResult.Opened control) {
            return null;
        }
        using (control.Handle) {
            RecapGridControlSnapshotResult snapshot = control.Handle.Reader
                .ReadSnapshot();
            if (snapshot is not RecapGridControlSnapshotResult.Available value
                || value.Snapshot.Head.TimelineId.Value != scope.TimelineId) {
                return null;
            }
            RegisteredGridRecipe? registered = value.Snapshot.Recipes
                .SingleOrDefault(recipe => recipe.Recipe.Digest.Value
                    == rootRecipeDigest);
            if (registered is null) {
                return null;
            }
            HistoryTimelineReaderOpenResult timelineOpened =
                HistoryTimelineMaintenance.OpenReader(repositoryPath, refId);
            if (timelineOpened is not HistoryTimelineReaderOpenResult.Opened
                    timeline) {
                return null;
            }
            using (timeline.Handle) {
                HistoryTimelineSnapshotResult timelineSnapshot = timeline.Handle
                    .Reader.ReadSnapshot();
                if (timelineSnapshot is not HistoryTimelineSnapshotResult
                        .Available head
                    || head.Head.TimelineId.Value != scope.TimelineId) {
                    return null;
                }
                IReadOnlyList<HistoryTimelineSelectedRow> selected =
                    ReadSelectedPath(timeline.Handle.Reader, head.Head);
                int index = selected.ToList().FindIndex(row =>
                    row.Descriptor.RowId.Value == historyRowId);
                if (index < 0) {
                    return null;
                }
                V4Row? predecessor = index == 0 ? null : rows.SingleOrDefault(
                    row => row.RefId == scope.RefId
                        && row.TimelineId == scope.TimelineId
                        && row.RecipeDigest == rootRecipeDigest
                        && row.HistoryRowId
                            == selected[index - 1].Descriptor.RowId.Value);
                if (index != 0 && predecessor is null) {
                    return null;
                }
                return CreateWork(scope, registered.Recipe, historyRowId,
                    predecessor, cells);
            }
        }
    }

    private static RowWork CreateWork(
        V4Scope scope,
        GridBuildRecipe recipe,
        string historyRowId,
        V4Row? predecessor,
        IReadOnlyList<V4Cell> cells
    ) {
        var partialByColumn = cells.ToDictionary(
            static cell => cell.LogicalColumnId, StringComparer.Ordinal);
        if (partialByColumn.Count != cells.Count) {
            throw new InvalidDataException(
                "V4 partial work has duplicate logical columns.");
        }
        var assignments = new List<RowWorkAssignment>(
            recipe.Target.OrderedColumns.Count);
        foreach (BuildTargetColumn target in recipe.Target.OrderedColumns) {
            if (partialByColumn.TryGetValue(target.LogicalColumnId.Value,
                    out V4Cell? cell)) {
                if (cell.DefinitionDigest != target.DefinitionDigest.Value) {
                    throw new InvalidDataException(
                        "V4 partial cell conflicts with the registered recipe target.");
                }
                if (recipe.Kind is GridBuildRecipeKind.Overlay
                    && !recipe.RecomputedColumns.Contains(
                        target.LogicalColumnId)) {
                    throw new InvalidDataException(
                        "V4 partial cell evaluates an Overlay reuse column.");
                }
                assignments.Add(new RowWorkAssignment(target.LogicalColumnId,
                    reusedCellId: null));
                continue;
            }
            if (recipe.Kind is GridBuildRecipeKind.Full
                || recipe.RecomputedColumns.Contains(target.LogicalColumnId)) {
                assignments.Add(new RowWorkAssignment(target.LogicalColumnId,
                    reusedCellId: null));
                continue;
            }
            V4Member member = predecessor?.Members.SingleOrDefault(value =>
                value.LogicalColumnId == target.LogicalColumnId.Value
                && value.DefinitionDigest == target.DefinitionDigest.Value)
                ?? throw new InvalidDataException(
                    "V4 Overlay partial has no provable reused predecessor cell.");
            assignments.Add(new RowWorkAssignment(target.LogicalColumnId,
                new CellId(member.CellId)));
        }
        return new RowWork(new RowWorkKey(ParseRef(scope.RefId),
                new TimelineId(scope.TimelineId), recipe.Digest,
                new HistoryRowId(historyRowId)), recipe.Target,
            predecessor is null ? null : new HistoryRowId(
                predecessor.HistoryRowId),
            predecessor is null ? null : new RowResultId(predecessor.Id),
            assignments);
    }

    private static IReadOnlyList<HistoryTimelineSelectedRow> ReadSelectedPath(
        HistoryTimelineReader reader,
        TimelineHeadRef head
    ) {
        var rows = new List<HistoryTimelineSelectedRow>();
        HistoryTimelinePathCursor? cursor = null;
        do {
            HistoryTimelinePathPageResult page = reader.ReadSelectedPathPage(
                head, cursor);
            if (page is not HistoryTimelinePathPageResult.Page available) {
                throw new InvalidDataException(
                    "V4 partial migration cannot read its selected Timeline path.");
            }
            rows.AddRange(available.Value.Rows);
            cursor = available.Value.Next;
        } while (cursor is not null);
        return rows;
    }

    private static IReadOnlyList<V4Cell> ReadPartialCells(
        SqliteConnection connection
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.cell_id,c.recipe_digest,c.history_row_id,
                   c.logical_column_id,c.definition_digest
            FROM cell_artifact c
            WHERE NOT EXISTS(
                SELECT 1 FROM row_view_member m WHERE m.cell_id=c.cell_id)
            ORDER BY c.recipe_digest,c.history_row_id,c.logical_column_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<V4Cell>();
        while (reader.Read()) {
            result.Add(new V4Cell(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        return result;
    }

    private static IReadOnlyList<V4Row> ReadRows(SqliteConnection connection) {
        var rows = new List<V4Row>();
        using (SqliteCommand command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT row_result_id,ref_id,timeline_id,history_row_id,
                       recipe_digest
                FROM row_view ORDER BY row_result_id;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                rows.Add(new V4Row(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }
        }
        foreach (V4Row row in rows) {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT logical_column_id,definition_digest,cell_id
                FROM row_view_member WHERE row_result_id=$id
                ORDER BY column_ordinal;
                """;
            command.Parameters.AddWithValue("$id", row.Id);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                row.Members.Add(new V4Member(reader.GetString(0),
                    reader.GetString(1), reader.GetString(2)));
            }
        }
        return rows;
    }

    private static int ReadSchemaVersion(SqliteConnection connection) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection OpenReadOnly(string path) {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 0
        }.ToString());
        connection.Open();
        return connection;
    }

    private static RefId ParseRef(string value) => new(ulong.Parse(value,
        System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture));

    private sealed record V4Scope(string RefId, string TimelineId);
    private sealed record V4Cell(string Id, string RecipeDigest,
        string HistoryRowId, string LogicalColumnId, string DefinitionDigest);
    private sealed record V4Member(string LogicalColumnId,
        string DefinitionDigest, string CellId);

    private sealed class V4Row(string id, string refId, string timelineId,
        string historyRowId, string recipeDigest) {
        internal string Id { get; } = id;
        internal string RefId { get; } = refId;
        internal string TimelineId { get; } = timelineId;
        internal string HistoryRowId { get; } = historyRowId;
        internal string RecipeDigest { get; } = recipeDigest;
        internal List<V4Member> Members { get; } = [];
    }
}
