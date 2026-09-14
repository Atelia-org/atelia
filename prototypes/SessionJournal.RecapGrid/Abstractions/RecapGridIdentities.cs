namespace Atelia.SessionJournal.RecapGrid;

public readonly record struct LogicalColumnId {
    public LogicalColumnId(string value) {
        Value = RecapGridSyntax.RequireIdentifier(value, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct FamilyDefinitionDigest {
    public FamilyDefinitionDigest(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct MaintainerDefinitionDigest {
    public MaintainerDefinitionDigest(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct BuildTargetDigest {
    public BuildTargetDigest(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct GridBuildRecipeDigest {
    public GridBuildRecipeDigest(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct CellId {
    public CellId(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 32, nameof(value));
    }

    internal static CellId Generate() => new(Guid.NewGuid().ToString("N"));

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct RowResultId {
    public RowResultId(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 32, nameof(value));
    }

    internal static RowResultId Generate() => new(Guid.NewGuid().ToString("N"));

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}
