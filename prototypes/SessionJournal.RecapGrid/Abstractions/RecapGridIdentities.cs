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

public readonly record struct PriorInputProjectionDigest {
    public PriorInputProjectionDigest(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct EvaluationKeyDigest {
    public EvaluationKeyDigest(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct ContentDigest {
    public ContentDigest(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct CellDigest {
    public CellDigest(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct RowViewDigest {
    public RowViewDigest(string value) {
        Value = RecapGridSyntax.RequireLowerHex(value, 64, nameof(value));
    }

    public string Value { get; }
    public override string ToString() => Value ?? string.Empty;
}
