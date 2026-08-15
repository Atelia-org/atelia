using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Control;

internal sealed class ControlState {
    internal const int SchemaVersion = 2;
    private readonly SortedDictionary<string, FamilyDefinition> _families;
    private readonly SortedDictionary<string, MaintainerDefinitionRevision>
        _definitions;
    private readonly SortedDictionary<string, RegisteredGridRecipe> _recipes;
    private readonly SortedDictionary<string, ControlOperationReceipt>
        _operationReceipts;

    private ControlState(
        ControlHeadRef head,
        SortedDictionary<string, FamilyDefinition> families,
        SortedDictionary<string, MaintainerDefinitionRevision> definitions,
        SortedDictionary<string, RegisteredGridRecipe> recipes,
        SortedDictionary<string, ControlOperationReceipt> operationReceipts,
        byte[] canonicalBytes
    ) {
        Head = head;
        _families = families;
        _definitions = definitions;
        _recipes = recipes;
        _operationReceipts = operationReceipts;
        CanonicalBytes = canonicalBytes;
    }

    internal ControlHeadRef Head { get; }
    internal IReadOnlyDictionary<string, FamilyDefinition> Families => _families;
    internal IReadOnlyDictionary<string, MaintainerDefinitionRevision>
        Definitions => _definitions;
    internal IReadOnlyDictionary<string, RegisteredGridRecipe> Recipes => _recipes;
    internal IReadOnlyDictionary<string, ControlOperationReceipt>
        OperationReceipts => _operationReceipts;
    internal byte[] CanonicalBytes { get; }

    internal static ControlState CreateEmpty(
        RefId refId,
        TimelineId timelineId,
        ControlInstanceId? instanceId = null,
        long generation = 0
    ) => Create(
        instanceId ?? ControlInstanceId.Generate(),
        refId,
        timelineId,
        generation,
        activeRecipeDigest: null,
        new SortedDictionary<string, FamilyDefinition>(StringComparer.Ordinal),
        new SortedDictionary<string, MaintainerDefinitionRevision>(
            StringComparer.Ordinal
        ),
        new SortedDictionary<string, RegisteredGridRecipe>(
            StringComparer.Ordinal
        ),
        new SortedDictionary<string, ControlOperationReceipt>(
            StringComparer.Ordinal
        )
    );

    internal ControlState WithFamily(FamilyDefinition value)
        => WithFamily(value, ControlStorageLimits.MaximumFamilyCount);

    internal ControlState WithFamilyForTest(
        FamilyDefinition value,
        int maximumCount
    ) => WithFamily(value, maximumCount);

    private ControlState WithFamily(
        FamilyDefinition value,
        int maximumCount
    ) {
        if (!_families.ContainsKey(value.Digest.Value)
            && _families.Count >= maximumCount) {
            throw new ControlLimitException("ControlFamilyCount");
        }
        return With(
            families: Add(_families, value.Digest.Value, value),
            definitions: _definitions,
            recipes: _recipes,
            operationReceipts: _operationReceipts,
            active: Head.ActiveRecipeDigest
        );
    }

    internal ControlState WithDefinition(MaintainerDefinitionRevision value)
        => WithDefinition(
            value,
            ControlStorageLimits.MaximumDefinitionCount
        );

    internal ControlState WithDefinitionForTest(
        MaintainerDefinitionRevision value,
        int maximumCount
    ) => WithDefinition(value, maximumCount);

    private ControlState WithDefinition(
        MaintainerDefinitionRevision value,
        int maximumCount
    ) {
        if (!_definitions.ContainsKey(value.Digest.Value)
            && _definitions.Count >= maximumCount) {
            throw new ControlLimitException("ControlDefinitionCount");
        }
        return With(
            families: _families,
            definitions: Add(_definitions, value.Digest.Value, value),
            recipes: _recipes,
            operationReceipts: _operationReceipts,
            active: Head.ActiveRecipeDigest
        );
    }

    internal ControlState WithRecipe(RegisteredGridRecipe value)
        => WithRecipe(value, ControlStorageLimits.MaximumRecipeCount);

    internal ControlState WithRecipeForTest(
        RegisteredGridRecipe value,
        int maximumCount
    ) => WithRecipe(value, maximumCount);

    private ControlState WithRecipe(
        RegisteredGridRecipe value,
        int maximumCount
    ) {
        if (!_recipes.ContainsKey(value.Recipe.Digest.Value)
            && _recipes.Count >= maximumCount) {
            throw new ControlLimitException("ControlRecipeCount");
        }
        return With(
            families: _families,
            definitions: _definitions,
            recipes: Add(_recipes, value.Recipe.Digest.Value, value),
            operationReceipts: _operationReceipts,
            active: Head.ActiveRecipeDigest
        );
    }

    internal ControlState WithActive(GridBuildRecipeDigest? digest)
        => With(
            _families,
            _definitions,
            _recipes,
            _operationReceipts,
            digest
        );

    internal ControlState WithNewInstance(long generation)
        => Create(
            ControlInstanceId.Generate(),
            Head.RefId,
            Head.TimelineId,
            generation,
            Head.ActiveRecipeDigest,
            Clone(_families),
            Clone(_definitions),
            Clone(_recipes),
            Clone(_operationReceipts)
        );

    internal bool TryGetOperationReceipt(
        string operationKey,
        out ControlOperationReceipt? receipt
    ) => _operationReceipts.TryGetValue(operationKey, out receipt);

    internal ControlState WithTerminalOperation(
        ControlOperationReceipt receipt,
        long generation
    ) => WithTerminalOperation(
        receipt,
        generation,
        ControlStorageLimits.MaximumOperationReceiptCount
    );

    internal ControlState WithTerminalOperationForTest(
        ControlOperationReceipt receipt,
        long generation,
        int maximumCount
    ) => WithTerminalOperation(receipt, generation, maximumCount);

    private ControlState WithTerminalOperation(
        ControlOperationReceipt receipt,
        long generation,
        int maximumCount
    ) {
        ArgumentNullException.ThrowIfNull(receipt);
        if (_operationReceipts.ContainsKey(receipt.OperationKey)) {
            throw new ControlStoreException(
                "ControlOperationReceiptDuplicate",
                "A terminal operation receipt already exists."
            );
        }
        if (_operationReceipts.Count >= maximumCount) {
            throw new ControlLimitException("ControlOperationReceiptCount");
        }
        return Create(
            Head.InstanceId,
            Head.RefId,
            Head.TimelineId,
            generation,
            Head.ActiveRecipeDigest,
            Clone(_families),
            Clone(_definitions),
            Clone(_recipes),
            Add(_operationReceipts, receipt.OperationKey, receipt)
        );
    }

    internal ControlState WithGenerationAndReceipts(
        ControlInstanceId instanceId,
        long generation,
        IReadOnlyDictionary<string, ControlOperationReceipt> receipts
    ) => Create(
        instanceId,
        Head.RefId,
        Head.TimelineId,
        generation,
        Head.ActiveRecipeDigest,
        Clone(_families),
        Clone(_definitions),
        Clone(_recipes),
        new SortedDictionary<string, ControlOperationReceipt>(
            receipts.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal
            ),
            StringComparer.Ordinal
        )
    );

    internal RecapGridControlSnapshot Snapshot() => new(
        Head,
        Array.AsReadOnly(_families.Values.ToArray()),
        Array.AsReadOnly(_definitions.Values.ToArray()),
        Array.AsReadOnly(_recipes.Values.ToArray())
    );

    internal static ControlState Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.Length is < 2
            || bytes.Length > ControlStorageLimits.MaximumStateCanonicalUtf8Bytes) {
            throw new ControlStoreException(
                "ControlStateLimitExceeded",
                "The canonical Control state exceeds the code-owned byte cap."
            );
        }
        ControlFileDto? dto;
        try {
            dto = JsonSerializer.Deserialize<ControlFileDto>(
                bytes,
                ControlJson.Options
            );
        }
        catch (JsonException exception) {
            throw new ControlStoreException(
                "ControlStateInvalid",
                "The Control state is not strict JSON.",
                exception
            );
        }
        if (dto is null
            || !bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(
                dto,
                ControlJson.Options
            ))) {
            throw new ControlStoreException(
                "ControlStateNonCanonical",
                "The Control state is not the exact canonical encoding."
            );
        }
        if (dto.SchemaVersion != SchemaVersion) {
            throw new ControlUnsupportedSchemaException(dto.SchemaVersion);
        }
        RequireSortedUnique(dto.Families.Select(static entry => entry.Digest));
        RequireSortedUnique(dto.Definitions.Select(static entry => entry.Digest));
        RequireSortedUnique(dto.Recipes.Select(static entry => entry.Digest));
        RequireSortedUnique(dto.OperationReceipts.Select(
            static entry => entry.OperationKey));
        if (dto.Families.Length > ControlStorageLimits.MaximumFamilyCount
            || dto.Definitions.Length > ControlStorageLimits.MaximumDefinitionCount
            || dto.Recipes.Length > ControlStorageLimits.MaximumRecipeCount
            || dto.OperationReceipts.Length
                > ControlStorageLimits.MaximumOperationReceiptCount) {
            throw new ControlStoreException(
                "ControlStateCountLimitExceeded",
                "A Control state collection exceeds its code-owned count cap."
            );
        }
        var families = new SortedDictionary<string, FamilyDefinition>(
            StringComparer.Ordinal
        );
        foreach (CanonicalEntryDto entry in dto.Families) {
            FamilyDefinition value = FamilyDefinition.DecodeCanonical(entry.Value);
            RequireEntryDigest(entry.Digest, value.Digest.Value);
            families.Add(entry.Digest, value);
        }
        var definitions = new SortedDictionary<string, MaintainerDefinitionRevision>(
            StringComparer.Ordinal
        );
        foreach (CanonicalEntryDto entry in dto.Definitions) {
            MaintainerDefinitionRevision value =
                MaintainerDefinitionRevision.DecodeCanonical(entry.Value);
            RequireEntryDigest(entry.Digest, value.Digest.Value);
            definitions.Add(entry.Digest, value);
        }
        var recipes = new SortedDictionary<string, RegisteredGridRecipe>(
            StringComparer.Ordinal
        );
        foreach (RecipeEntryDto entry in dto.Recipes) {
            GridBuildRecipe value = GridBuildRecipe.DecodeCanonical(entry.Value);
            RequireEntryDigest(entry.Digest, value.Digest.Value);
            TimelineHeadRef head = TimelineHead(entry.Bootstrap.TimelineHead);
            HistoryRowId? rowId = entry.Bootstrap.RowId is null
                ? null
                : new HistoryRowId(entry.Bootstrap.RowId);
            HistorySegmentDescriptorDigest? descriptorDigest =
                entry.Bootstrap.DescriptorDigest is null
                    ? null
                    : new HistorySegmentDescriptorDigest(
                        entry.Bootstrap.DescriptorDigest
                    );
            if ((rowId is null) != (descriptorDigest is null)
                || rowId != value.BootstrapThroughRowId
                || value.TimelineId != head.TimelineId) {
                throw new ControlStoreException(
                    "RecipeBootstrapInvalid",
                    "The recipe bootstrap fields disagree about empty state."
                );
            }
            recipes.Add(
                entry.Digest,
                new RegisteredGridRecipe(
                    value,
                    new RegisteredRecipeBootstrap(
                        head,
                        rowId,
                        descriptorDigest
                    )
                )
            );
        }
        var receipts = new SortedDictionary<string, ControlOperationReceipt>(
            StringComparer.Ordinal
        );
        foreach (ControlOperationReceiptDto entry in dto.OperationReceipts) {
            ControlOperationReceipt receipt = DecodeReceipt(entry);
            receipts.Add(receipt.OperationKey, receipt);
        }
        foreach (MaintainerDefinitionRevision definition in definitions.Values) {
            if (!families.ContainsKey(definition.FamilyDigest.Value)) {
                throw new ControlStoreException(
                    "DefinitionFamilyAbsent",
                    "A stored definition references an absent family."
                );
            }
        }
        foreach (RegisteredGridRecipe registered in recipes.Values) {
            foreach (BuildTargetColumn column
                     in registered.Recipe.Target.OrderedColumns) {
                if (!definitions.ContainsKey(column.DefinitionDigest.Value)) {
                    throw new ControlStoreException(
                        "RecipeDefinitionAbsent",
                        "A stored recipe references an absent definition."
                    );
                }
            }
            GridBuildRecipe? baseRecipe = registered.Recipe.BaseRecipeDigest is { } digest
                && recipes.TryGetValue(digest.Value, out RegisteredGridRecipe? found)
                    ? found.Recipe
                    : null;
            try {
                registered.Recipe.ValidateBase(baseRecipe);
            }
            catch (ArgumentException exception) {
                throw new ControlStoreException(
                    "RecipeGraphInvalid",
                    "A stored recipe has an invalid base graph.",
                    exception
                );
            }
        }
        GridBuildRecipeDigest? active = dto.Head.ActiveRecipeDigest is null
            ? null
            : new GridBuildRecipeDigest(dto.Head.ActiveRecipeDigest);
        if (active is { } activeDigest
            && !recipes.ContainsKey(activeDigest.Value)) {
            throw new ControlStoreException(
                "ActiveRecipeAbsent",
                "The active recipe is absent from the stored closure."
            );
        }
        ControlState valueState = Create(
            new ControlInstanceId(dto.Head.InstanceId),
            new RefId(dto.Head.RefId),
            new TimelineId(dto.Head.TimelineId),
            dto.Head.Generation,
            active,
            families,
            definitions,
            recipes,
            receipts
        );
        if (!string.Equals(
                valueState.Head.StateDigest.Value,
                dto.Head.StateDigest,
                StringComparison.Ordinal)) {
            throw new ControlStoreException(
                "ControlStateDigestMismatch",
                "The Control head digest differs from its canonical state."
            );
        }
        return valueState;
    }

    private ControlState With(
        SortedDictionary<string, FamilyDefinition> families,
        SortedDictionary<string, MaintainerDefinitionRevision> definitions,
        SortedDictionary<string, RegisteredGridRecipe> recipes,
        SortedDictionary<string, ControlOperationReceipt> operationReceipts,
        GridBuildRecipeDigest? active
    ) => Create(
        Head.InstanceId,
        Head.RefId,
        Head.TimelineId,
        checked(Head.Generation + 1),
        active,
        Clone(families),
        Clone(definitions),
        Clone(recipes),
        Clone(operationReceipts)
    );

    private static ControlState Create(
        ControlInstanceId instanceId,
        RefId refId,
        TimelineId timelineId,
        long generation,
        GridBuildRecipeDigest? activeRecipeDigest,
        SortedDictionary<string, FamilyDefinition> families,
        SortedDictionary<string, MaintainerDefinitionRevision> definitions,
        SortedDictionary<string, RegisteredGridRecipe> recipes,
        SortedDictionary<string, ControlOperationReceipt> operationReceipts
    ) {
        ValidateGraph(
            refId,
            timelineId,
            activeRecipeDigest,
            families,
            definitions,
            recipes,
            operationReceipts
        );
        ControlBodyDto body = Body(
            instanceId,
            refId,
            timelineId,
            generation,
            activeRecipeDigest,
            families,
            definitions,
            recipes,
            operationReceipts
        );
        ControlStateDigest digest = new(Hash(
            "atelia.recap-grid.control-state.v2",
            JsonSerializer.SerializeToUtf8Bytes(body, ControlJson.Options)
        ));
        ControlHeadRef head = new(
            instanceId,
            refId,
            timelineId,
            generation,
            digest,
            activeRecipeDigest
        );
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            new ControlFileDto(
                SchemaVersion,
                new ControlHeadDto(
                    instanceId.Value,
                    refId.Packed,
                    timelineId.Value,
                    generation,
                    digest.Value,
                    activeRecipeDigest?.Value
                ),
                body.Families,
                body.Definitions,
                body.Recipes,
                body.OperationReceipts
            ),
            ControlJson.Options
        );
        if (canonical.Length
            > ControlStorageLimits.MaximumStateCanonicalUtf8Bytes) {
            throw new ControlLimitException("ControlStateBytes");
        }
        return new ControlState(
            head,
            families,
            definitions,
            recipes,
            operationReceipts,
            canonical
        );
    }

    private static void ValidateGraph(
        RefId refId,
        TimelineId timelineId,
        GridBuildRecipeDigest? activeRecipeDigest,
        SortedDictionary<string, FamilyDefinition> families,
        SortedDictionary<string, MaintainerDefinitionRevision> definitions,
        SortedDictionary<string, RegisteredGridRecipe> recipes,
        SortedDictionary<string, ControlOperationReceipt> operationReceipts
    ) {
        if (families.Count > ControlStorageLimits.MaximumFamilyCount
            || definitions.Count
                > ControlStorageLimits.MaximumDefinitionCount
            || recipes.Count > ControlStorageLimits.MaximumRecipeCount
            || operationReceipts.Count
                > ControlStorageLimits.MaximumOperationReceiptCount) {
            throw new ControlLimitException("ControlStateEntryCount");
        }
        foreach ((string key, FamilyDefinition family) in families) {
            RequireEntryDigest(key, family.Digest.Value);
        }
        foreach ((string key, MaintainerDefinitionRevision definition)
                 in definitions) {
            RequireEntryDigest(key, definition.Digest.Value);
            if (!families.ContainsKey(definition.FamilyDigest.Value)) {
                throw new ControlStoreException(
                    "DefinitionFamilyAbsent",
                    "A definition references an absent family."
                );
            }
        }
        foreach ((string key, RegisteredGridRecipe registered) in recipes) {
            GridBuildRecipe recipe = registered.Recipe;
            RequireEntryDigest(key, recipe.Digest.Value);
            if (recipe.TimelineId != timelineId
                || registered.Bootstrap.TimelineHead.RefId != refId
                || registered.Bootstrap.TimelineHead.TimelineId
                    != timelineId
                || registered.Bootstrap.RowId
                    != recipe.BootstrapThroughRowId
                || (registered.Bootstrap.RowId is null)
                    != (registered.Bootstrap.DescriptorDigest is null)) {
                throw new ControlStoreException(
                    "RecipeScopeInvalid",
                    "A recipe or bootstrap belongs to another Control scope."
                );
            }
            foreach (BuildTargetColumn column
                     in recipe.Target.OrderedColumns) {
                if (!definitions.TryGetValue(
                        column.DefinitionDigest.Value,
                        out MaintainerDefinitionRevision? definition)) {
                    throw new ControlStoreException(
                        "RecipeDefinitionAbsent",
                        "A recipe references an absent definition."
                    );
                }
                if (column.LogicalColumnId
                    != definition.LogicalColumnId) {
                    throw new ControlStoreException(
                        "RecipeColumnDefinitionMismatch",
                        "A recipe column differs from its referenced definition."
                    );
                }
            }
            GridBuildRecipe? baseRecipe = recipe.BaseRecipeDigest is { } digest
                && recipes.TryGetValue(
                    digest.Value,
                    out RegisteredGridRecipe? found)
                    ? found.Recipe
                    : null;
            try {
                recipe.ValidateBase(baseRecipe);
            }
            catch (ArgumentException exception) {
                throw new ControlStoreException(
                    "RecipeGraphInvalid",
                    "A recipe has an invalid base graph.",
                    exception
                );
            }
            RequireBoundedBaseDepth(recipe, recipes);
        }
        if (activeRecipeDigest is { } active
            && !recipes.ContainsKey(active.Value)) {
            throw new ControlStoreException(
                "ActiveRecipeAbsent",
                "The active recipe is absent from the Control state."
            );
        }
        foreach ((string key, ControlOperationReceipt receipt)
                 in operationReceipts) {
            ValidateReceipt(key, receipt);
        }
    }

    private static void RequireBoundedBaseDepth(
        GridBuildRecipe recipe,
        IReadOnlyDictionary<string, RegisteredGridRecipe> recipes
    ) {
        var seen = new HashSet<GridBuildRecipeDigest>();
        GridBuildRecipe current = recipe;
        int depth = 0;
        while (current.BaseRecipeDigest is { } baseDigest) {
            if (!seen.Add(baseDigest)
                || ++depth > ControlStorageLimits.MaximumRecipeBaseDepth
                || !recipes.TryGetValue(
                    baseDigest.Value,
                    out RegisteredGridRecipe? next)) {
                throw new ControlStoreException(
                    "RecipeBaseDepthInvalid",
                    "The recipe base chain is cyclic, absent, or too deep."
                );
            }
            current = next.Recipe;
        }
    }

    private static ControlBodyDto Body(
        ControlInstanceId instanceId,
        RefId refId,
        TimelineId timelineId,
        long generation,
        GridBuildRecipeDigest? activeRecipeDigest,
        SortedDictionary<string, FamilyDefinition> families,
        SortedDictionary<string, MaintainerDefinitionRevision> definitions,
        SortedDictionary<string, RegisteredGridRecipe> recipes,
        SortedDictionary<string, ControlOperationReceipt> operationReceipts
    ) => new(
        SchemaVersion,
        instanceId.Value,
        refId.Packed,
        timelineId.Value,
        generation,
        activeRecipeDigest?.Value,
        families.Select(static pair => new CanonicalEntryDto(
            pair.Key,
            pair.Value.ToCanonicalBytes()
        )).ToArray(),
        definitions.Select(static pair => new CanonicalEntryDto(
            pair.Key,
            pair.Value.ToCanonicalBytes()
        )).ToArray(),
        recipes.Select(static pair => new RecipeEntryDto(
            pair.Key,
            pair.Value.Recipe.ToCanonicalBytes(),
            new RecipeBootstrapDto(
                TimelineHead(pair.Value.Bootstrap.TimelineHead),
                pair.Value.Bootstrap.RowId?.Value,
                pair.Value.Bootstrap.DescriptorDigest?.Value
            )
        )).ToArray(),
        operationReceipts.Select(static pair => ReceiptDto(pair.Value))
            .ToArray()
    );

    private static SortedDictionary<string, T> Add<T>(
        SortedDictionary<string, T> source,
        string key,
        T value
    ) {
        SortedDictionary<string, T> copy = Clone(source);
        copy.Add(key, value);
        return copy;
    }

    private static SortedDictionary<string, T> Clone<T>(
        SortedDictionary<string, T> source
    ) => new(source, StringComparer.Ordinal);

    private static void RequireSortedUnique(IEnumerable<string> keys) {
        string? previous = null;
        foreach (string key in keys) {
            if (previous is not null
                && string.CompareOrdinal(previous, key) >= 0) {
                throw new ControlStoreException(
                    "ControlStateOrderInvalid",
                    "Canonical Control state collections must be strictly digest-sorted."
                );
            }
            previous = key;
        }
    }

    private static void RequireEntryDigest(string key, string actual) {
        if (!string.Equals(key, actual, StringComparison.Ordinal)) {
            throw new ControlStoreException(
                "ControlEntryDigestMismatch",
                "A Control state entry key differs from its canonical value."
            );
        }
    }

    private static ControlOperationReceipt DecodeReceipt(
        ControlOperationReceiptDto value
    ) {
        ControlOperationReceipt receipt;
        try {
            receipt = new ControlOperationReceipt(
                value.OperationKey,
                value.ExecutionSequence,
                value.RuntimeIdentityDigest,
                value.CommandDigest,
                value.ResultIdentity,
                new ControlInstanceId(value.OriginalInstanceId),
                value.OriginalGeneration
            );
        }
        catch (ArgumentException exception) {
            throw new ControlStoreException(
                "ControlOperationReceiptInvalid",
                "A terminal operation receipt contains an invalid identity.",
                exception
            );
        }
        ValidateReceipt(value.OperationKey, receipt);
        return receipt;
    }

    private static void ValidateReceipt(
        string key,
        ControlOperationReceipt receipt
    ) {
        try {
            RecapGridControlOperation.RequireSha256(
                key,
                nameof(receipt.OperationKey)
            );
            RecapGridControlOperation.RequireSha256(
                receipt.RuntimeIdentityDigest,
                nameof(receipt.RuntimeIdentityDigest)
            );
            RecapGridControlOperation.RequireSha256(
                receipt.CommandDigest,
                nameof(receipt.CommandDigest)
            );
            RecapGridControlOperation.RequireSha256(
                receipt.ResultIdentity,
                nameof(receipt.ResultIdentity)
            );
        }
        catch (ArgumentException exception) {
            throw new ControlStoreException(
                "ControlOperationReceiptInvalid",
                "A terminal operation receipt contains a non-canonical digest.",
                exception
            );
        }
        if (!string.Equals(key, receipt.OperationKey,
                StringComparison.Ordinal)
            || receipt.ExecutionSequence <= 0
            || receipt.OriginalInstanceId.Value is null
            || receipt.OriginalGeneration < 0) {
            throw new ControlStoreException(
                "ControlOperationReceiptInvalid",
                "A terminal operation receipt is structurally invalid."
            );
        }
    }

    private static ControlOperationReceiptDto ReceiptDto(
        ControlOperationReceipt value
    ) => new(
        value.OperationKey,
        value.ExecutionSequence,
        value.RuntimeIdentityDigest,
        value.CommandDigest,
        value.ResultIdentity,
        value.OriginalInstanceId.Value,
        value.OriginalGeneration
    );

    private static TimelineHeadDto TimelineHead(TimelineHeadRef value) => new(
        value.TimelineId.Value,
        value.RefId.Packed,
        value.HeadRowId?.Value,
        value.ActivePartitionPolicyDigest,
        value.SelectedRawHeadAtCommit?.Ticket.Packed,
        value.SelectedRawHeadAtCommit?.SegmentNumber,
        value.SelectedRawHeadAtCommit?.Hint.Packed,
        value.SelectedPathCount,
        value.SelectedPathDigest,
        value.Generation
    );

    private static TimelineHeadRef TimelineHead(TimelineHeadDto value) {
        EventAddress? rawHead = value.RawTicket is null
            ? null
            : new EventAddress(
                SizedPtr.FromPacked(value.RawTicket.Value),
                value.RawSegment!.Value,
                new AddressHint(value.RawHint!.Value)
            );
        if ((value.RawTicket is null) != (value.RawSegment is null)
            || (value.RawTicket is null) != (value.RawHint is null)) {
            throw new ControlStoreException(
                "TimelineHeadAddressInvalid",
                "A stored Timeline raw-head address is partial."
            );
        }
        return new TimelineHeadRef(
            new TimelineId(value.TimelineId),
            new RefId(value.RefId),
            value.HeadRowId is null ? null : new HistoryRowId(value.HeadRowId),
            value.ActivePolicyDigest,
            rawHead,
            value.SelectedPathCount,
            value.SelectedPathDigest,
            value.Generation
        );
    }

    private static string Hash(string domain, ReadOnlySpan<byte> value) {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        Append(Encoding.UTF8.GetBytes(domain));
        Append(value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(ReadOnlySpan<byte> bytes) {
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                length,
                bytes.Length
            );
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}

internal static class ControlJson {
    internal static JsonSerializerOptions Options { get; } = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

internal static class ControlStorageLimits {
    // V1 activation policy. Keep this semantic owner local to Control; the
    // neutral SessionJournal carrier contract is verified equal by a
    // cross-project contract test.
    internal const int MaximumContextComposableContentUtf8Bytes = 256 * 1024;
    internal const int MaximumStateCanonicalUtf8Bytes = 32 * 1024 * 1024;
    internal const int MaximumFamilyCount = 256;
    internal const int MaximumDefinitionCount = 4_096;
    internal const int MaximumRecipeCount = 4_096;
    internal const int MaximumOperationReceiptCount = 16_384;
    internal const int MaximumRecipeBaseDepth = 256;
    internal const int MaximumBackupManifestUtf8Bytes = 16 * 1024;
}

internal sealed record CanonicalEntryDto(string Digest, byte[] Value);
internal sealed record RecipeEntryDto(
    string Digest,
    byte[] Value,
    RecipeBootstrapDto Bootstrap
);
internal sealed record RecipeBootstrapDto(
    TimelineHeadDto TimelineHead,
    string? RowId,
    string? DescriptorDigest
);
internal sealed record ControlOperationReceiptDto(
    string OperationKey,
    long ExecutionSequence,
    string RuntimeIdentityDigest,
    string CommandDigest,
    string ResultIdentity,
    string OriginalInstanceId,
    long OriginalGeneration
);
internal sealed record TimelineHeadDto(
    string TimelineId,
    ulong RefId,
    string? HeadRowId,
    string ActivePolicyDigest,
    ulong? RawTicket,
    uint? RawSegment,
    uint? RawHint,
    long SelectedPathCount,
    string SelectedPathDigest,
    long Generation
);
internal sealed record ControlHeadDto(
    string InstanceId,
    ulong RefId,
    string TimelineId,
    long Generation,
    string StateDigest,
    string? ActiveRecipeDigest
);
internal sealed record ControlBodyDto(
    int SchemaVersion,
    string InstanceId,
    ulong RefId,
    string TimelineId,
    long Generation,
    string? ActiveRecipeDigest,
    CanonicalEntryDto[] Families,
    CanonicalEntryDto[] Definitions,
    RecipeEntryDto[] Recipes,
    ControlOperationReceiptDto[] OperationReceipts
);
internal sealed record ControlFileDto(
    int SchemaVersion,
    ControlHeadDto Head,
    CanonicalEntryDto[] Families,
    CanonicalEntryDto[] Definitions,
    RecipeEntryDto[] Recipes,
    ControlOperationReceiptDto[] OperationReceipts
);

internal sealed record ControlOperationReceipt(
    string OperationKey,
    long ExecutionSequence,
    string RuntimeIdentityDigest,
    string CommandDigest,
    string ResultIdentity,
    ControlInstanceId OriginalInstanceId,
    long OriginalGeneration
);

internal sealed class ControlStoreException : Exception {
    internal ControlStoreException(
        string code,
        string message,
        Exception? inner = null
    ) : base(message, inner) {
        Code = code;
    }

    internal string Code { get; }
}

internal sealed class ControlUnsupportedSchemaException(int version)
    : Exception {
    internal int Version { get; } = version;
}

internal sealed class ControlLimitException(string limit)
    : InvalidOperationException {
    internal string Limit { get; } = limit;
}
