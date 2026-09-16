using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Data.Sqlite;

namespace Atelia.SessionJournal.Cli;

/// <summary>Proves V4 partial cells from exact, inventory-discovered durable scopes only.</summary>
internal static class RecapGridV4PartialWorkProofResolver {
    internal static IReadOnlyList<RowWork> Resolve(string repositoryPath) {
        string database = Path.Combine(repositoryPath, "derived", "recap-grid", "v1", "grid.sqlite");
        if (!File.Exists(database)) return [];
        using SqliteConnection connection = OpenReadOnly(database);
        if (ReadSchemaVersion(connection) != 4) return [];
        IReadOnlyList<V4Cell> partial = ReadPartialCells(connection);
        if (partial.Count == 0) return [];
        IReadOnlyList<V4Row> rows = ReadRows(connection);
        V4Scope[] scopes = InventoryIntersection(repositoryPath);
        return partial.GroupBy(static x => (x.RecipeDigest, x.HistoryRowId))
            .Select(x => ResolveGroup(repositoryPath, scopes, rows, x.Key.RecipeDigest, x.Key.HistoryRowId, x.ToArray())).ToArray();
    }

    private static V4Scope[] InventoryIntersection(string repositoryPath) {
        HistoryTimelineScopeInventoryResult timelines = HistoryTimelineMaintenance.InventoryScopes(repositoryPath);
        if (timelines is HistoryTimelineScopeInventoryResult.Invalid invalidTimeline) throw Unprovable($"Timeline scope inventory invalid: {invalidTimeline.Code}: {invalidTimeline.Detail}");
        if (timelines is not HistoryTimelineScopeInventoryResult.Available t) throw Unavailable("Timeline scope inventory is temporarily unavailable.");
        RecapGridControlScopeInventoryResult controls = RecapGridControlMaintenance.InventoryScopes(repositoryPath);
        if (controls is RecapGridControlScopeInventoryResult.Invalid invalidControl) throw Unprovable($"Control scope inventory invalid: {invalidControl.Code}: {invalidControl.Detail}");
        if (controls is not RecapGridControlScopeInventoryResult.Available c) throw Unavailable("Control scope inventory is temporarily unavailable.");
        var control = c.Scopes.Select(static x => new V4Scope(x.RefId.ToHexString(), x.TimelineId.Value)).ToHashSet();
        return t.Scopes.Select(static x => new V4Scope(x.RefId.ToHexString(), x.TimelineId.Value))
            .Where(control.Contains).OrderBy(static x => x.RefId, StringComparer.Ordinal).ThenBy(static x => x.TimelineId, StringComparer.Ordinal).ToArray();
    }

    private static RowWork ResolveGroup(string repositoryPath, IReadOnlyList<V4Scope> scopes, IReadOnlyList<V4Row> rows, string root, string history, IReadOnlyList<V4Cell> cells) {
        RowWork[] candidates = scopes.Select(x => TryResolveScope(repositoryPath, x, rows, root, history, cells)).Where(static x => x is not null).Cast<RowWork>().ToArray();
        return candidates.Length switch {
            1 => candidates[0],
            0 => throw Unprovable("No exact Control/Timeline scope can prove this V4 partial cell."),
            _ => throw Ambiguous("Multiple exact Control/Timeline scopes can prove this V4 partial cell.")
        };
    }

    private static RowWork? TryResolveScope(string repositoryPath, V4Scope scope, IReadOnlyList<V4Row> rows, string root, string history, IReadOnlyList<V4Cell> cells) {
        RefId refId = ParseRef(scope.RefId);
        RecapGridControlReaderOpenResult controlOpened = RecapGridControlMaintenance.OpenExactReader(repositoryPath, refId, new TimelineId(scope.TimelineId));
        if (controlOpened is not RecapGridControlReaderOpenResult.Opened control) {
            if (controlOpened is RecapGridControlReaderOpenResult.Busy) throw Unavailable("An inventoried Control scope is busy.");
            throw Unprovable($"An inventoried Control scope cannot be opened exactly: {controlOpened}.");
        }
        using (control.Handle) {
            RecapGridControlSnapshotResult snapshotResult = control.Handle.Reader.ReadSnapshot();
            if (snapshotResult is not RecapGridControlSnapshotResult.Available snapshot) {
                if (snapshotResult is RecapGridControlSnapshotResult.Busy or RecapGridControlSnapshotResult.Disposed) throw Unavailable($"An inventoried Control scope is temporarily unreadable: {snapshotResult}.");
                throw Unprovable($"An inventoried Control scope is invalid: {snapshotResult}.");
            }
            RegisteredGridRecipe[] registeredMatches = snapshot.Snapshot.Recipes.Where(x => x.Recipe.Digest.Value == root).ToArray();
            if (registeredMatches.Length > 1) throw Unprovable("Control has duplicate registrations for the V4 root recipe.");
            RegisteredGridRecipe? registered = registeredMatches.SingleOrDefault();
            if (registered is null || registered.Recipe.TimelineId.Value != scope.TimelineId) return null;
            HistoryTimelineExactReaderOpenResult timelineOpened = HistoryTimelineMaintenance.OpenExactReader(repositoryPath, refId, new TimelineId(scope.TimelineId));
            if (timelineOpened is not HistoryTimelineExactReaderOpenResult.Opened timeline) {
                if (timelineOpened is HistoryTimelineExactReaderOpenResult.Busy) throw Unavailable("An inventoried Timeline scope is busy.");
                throw Unprovable($"An inventoried Timeline scope cannot be opened exactly: {timelineOpened}.");
            }
            using (timeline.Handle) {
                HistoryTimelineSnapshotResult timelineSnapshot = timeline.Handle.Reader.ReadSnapshot();
                if (timelineSnapshot is not HistoryTimelineSnapshotResult.Available head) {
                    if (timelineSnapshot is HistoryTimelineSnapshotResult.Busy) throw Unavailable("An inventoried Timeline scope is busy.");
                    throw Unprovable($"An inventoried Timeline scope is invalid: {timelineSnapshot}.");
                }
                IReadOnlyList<HistoryTimelineSelectedRow> selected = ReadSelectedPath(timeline.Handle.Reader, head.Head);
                int index = selected.ToList().FindIndex(x => x.Descriptor.RowId.Value == history);
                if (index < 0) return null;
                V4Row? prior = index == 0 ? null : UniqueRow(rows, scope, root, selected[index - 1].Descriptor.RowId.Value);
                if (index != 0 && prior is null) return null;
                return CreateWork(scope, registered.Recipe, snapshot.Snapshot.Recipes, selected, index, prior, cells, rows);
            }
        }
    }

    private static RowWork CreateWork(V4Scope scope, GridBuildRecipe recipe, IReadOnlyList<RegisteredGridRecipe> recipes, IReadOnlyList<HistoryTimelineSelectedRow> selected, int index, V4Row? prior, IReadOnlyList<V4Cell> cells, IReadOnlyList<V4Row> rows) {
        string history = selected[index].Descriptor.RowId.Value;
        var partial = cells.ToDictionary(static x => x.LogicalColumnId, StringComparer.Ordinal);
        if (partial.Count != cells.Count) throw Unprovable("V4 partial work has duplicate logical columns.");
        bool prefix = false; GridBuildRecipe? baseRecipe = null;
        if (recipe.Kind is GridBuildRecipeKind.Overlay) {
            if (recipe.BootstrapThroughRowId is not { } through) throw Unprovable("V4 Overlay lacks its bootstrap boundary.");
            int throughIndex = selected.ToList().FindIndex(x => x.Descriptor.RowId == through);
            if (throughIndex < 0) throw Unprovable("V4 Overlay bootstrap row is absent from its selected path.");
            prefix = index <= throughIndex;
            RegisteredGridRecipe[] baseMatches = recipes.Where(x => x.Recipe.Digest == recipe.BaseRecipeDigest).ToArray();
            if (baseMatches.Length > 1) throw Unprovable("Control has duplicate registrations for the V4 Overlay base recipe.");
            baseRecipe = baseMatches.SingleOrDefault()?.Recipe ?? throw Unprovable("V4 Overlay base recipe is absent from Control.");
        }
        var assignments = new List<RowWorkAssignment>(recipe.Target.OrderedColumns.Count);
        foreach (BuildTargetColumn target in recipe.Target.OrderedColumns) {
            bool reuse = recipe.Kind is GridBuildRecipeKind.Overlay && prefix && !recipe.RecomputedColumns.Contains(target.LogicalColumnId);
            if (!reuse) {
                if (partial.TryGetValue(target.LogicalColumnId.Value, out V4Cell? cell) && cell.DefinitionDigest != target.DefinitionDigest.Value) throw Unprovable("V4 partial cell conflicts with its registered producer target.");
                assignments.Add(new RowWorkAssignment(target.LogicalColumnId, null));
                continue;
            }
            if (partial.ContainsKey(target.LogicalColumnId.Value)) throw Unprovable("V4 Overlay partial evaluates a bootstrap reuse column.");
            V4Row? baseRow = UniqueRow(rows, scope, baseRecipe!.Digest.Value, history);
            V4Member? member = baseRow is null ? null : UniqueMember(baseRow, target.LogicalColumnId.Value, target.DefinitionDigest.Value);
            if (member is null) throw Unprovable("V4 Overlay has no exact same-row base-cell proof.");
            assignments.Add(new RowWorkAssignment(target.LogicalColumnId, new CellId(member.CellId)));
        }
        return new RowWork(new RowWorkKey(ParseRef(scope.RefId), new TimelineId(scope.TimelineId), recipe.Digest, new HistoryRowId(history)), recipe.Target, prior is null ? null : new HistoryRowId(prior.HistoryRowId), prior is null ? null : new RowResultId(prior.Id), assignments);
    }

    private static V4Row? UniqueRow(IReadOnlyList<V4Row> rows, V4Scope scope, string root, string history) {
        V4Row[] found = rows.Where(x => x.RefId == scope.RefId && x.TimelineId == scope.TimelineId && x.RecipeDigest == root && x.HistoryRowId == history).ToArray();
        return found.Length switch { 0 => null, 1 => found[0], _ => throw Unprovable("V4 has multiple rows for an exact proof coordinate.") };
    }
    private static V4Member? UniqueMember(V4Row row, string logical, string definition) {
        V4Member[] found = row.Members.Where(x => x.LogicalColumnId == logical && x.DefinitionDigest == definition).ToArray();
        return found.Length switch { 0 => null, 1 => found[0], _ => throw Unprovable("V4 base row has duplicate reusable members.") };
    }
    private static IReadOnlyList<HistoryTimelineSelectedRow> ReadSelectedPath(HistoryTimelineReader reader, TimelineHeadRef head) {
        var rows = new List<HistoryTimelineSelectedRow>(); HistoryTimelinePathCursor? cursor = null;
        do { HistoryTimelinePathPageResult page = reader.ReadSelectedPathPage(head, cursor); if (page is not HistoryTimelinePathPageResult.Page available) throw Unavailable("Timeline selected path cannot be read exactly."); rows.AddRange(available.Value.Rows); cursor = available.Value.Next; } while (cursor is not null);
        return rows;
    }
    private static IReadOnlyList<V4Cell> ReadPartialCells(SqliteConnection connection) {
        using SqliteCommand command = connection.CreateCommand(); command.CommandText = "SELECT c.cell_id,c.recipe_digest,c.history_row_id,c.logical_column_id,c.definition_digest FROM cell_artifact c WHERE NOT EXISTS(SELECT 1 FROM row_view_member m WHERE m.cell_id=c.cell_id) ORDER BY c.recipe_digest,c.history_row_id,c.logical_column_id;"; using SqliteDataReader reader = command.ExecuteReader(); var result = new List<V4Cell>(); while (reader.Read()) result.Add(new V4Cell(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))); return result;
    }
    private static IReadOnlyList<V4Row> ReadRows(SqliteConnection connection) {
        var rows = new List<V4Row>(); using (SqliteCommand command = connection.CreateCommand()) { command.CommandText = "SELECT row_result_id,ref_id,timeline_id,history_row_id,recipe_digest FROM row_view ORDER BY row_result_id;"; using SqliteDataReader reader = command.ExecuteReader(); while (reader.Read()) rows.Add(new V4Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))); }
        foreach (V4Row row in rows) { using SqliteCommand command = connection.CreateCommand(); command.CommandText = "SELECT logical_column_id,definition_digest,cell_id FROM row_view_member WHERE row_result_id=$id ORDER BY column_ordinal;"; command.Parameters.AddWithValue("$id", row.Id); using SqliteDataReader reader = command.ExecuteReader(); while (reader.Read()) row.Members.Add(new V4Member(reader.GetString(0), reader.GetString(1), reader.GetString(2))); }
        return rows;
    }
    private static int ReadSchemaVersion(SqliteConnection connection) { using SqliteCommand command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version;"; return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); }
    private static SqliteConnection OpenReadOnly(string path) { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 0 }.ToString()); connection.Open(); return connection; }
    private static RefId ParseRef(string value) => new(ulong.Parse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
    private static RecapGridStorePartialProofException Unprovable(string detail) => new(RecapGridStorePartialProofFailure.Unprovable, detail);
    private static RecapGridStorePartialProofException Ambiguous(string detail) => new(RecapGridStorePartialProofFailure.Ambiguous, detail);
    private static RecapGridStorePartialProofException Unavailable(string detail) => new(RecapGridStorePartialProofFailure.Unavailable, detail);
    private sealed record V4Scope(string RefId, string TimelineId);
    private sealed record V4Cell(string Id, string RecipeDigest, string HistoryRowId, string LogicalColumnId, string DefinitionDigest);
    private sealed record V4Member(string LogicalColumnId, string DefinitionDigest, string CellId);
    private sealed class V4Row(string id, string refId, string timelineId, string historyRowId, string recipeDigest) { internal string Id { get; } = id; internal string RefId { get; } = refId; internal string TimelineId { get; } = timelineId; internal string HistoryRowId { get; } = historyRowId; internal string RecipeDigest { get; } = recipeDigest; internal List<V4Member> Members { get; } = []; }
}
