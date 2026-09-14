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

    /// <summary>
    /// Computes the existing prior-input identity from ordered cell content.
    /// Cell provenance and definition identity do not participate in this digest.
    /// </summary>
    public static PriorInputProjectionDigest FromCells(
        IReadOnlyList<RecapCellArtifact> orderedCells
    ) {
        ArgumentNullException.ThrowIfNull(orderedCells);
        int count = orderedCells.Count;
        if (count > RecapGridLimits.MaximumColumnCount) {
            throw new ArgumentOutOfRangeException(
                nameof(orderedCells),
                $"The sequence exceeds {RecapGridLimits.MaximumColumnCount} members."
            );
        }
        var columns = new HashSet<LogicalColumnId>();
        var content = new PriorProjectedContentDto[count];
        for (int index = 0; index < count; index++) {
            RecapCellArtifact cell = orderedCells[index];
            if (cell is null || !columns.Add(cell.LogicalColumnId)) {
                throw new ArgumentException(
                    "Prior cells must be non-null and logically unique.",
                    nameof(orderedCells)
                );
            }
            content[index] = new PriorProjectedContentDto(
                cell.LogicalColumnId.Value,
                cell.ContentDigest.Value
            );
        }
        return new PriorInputProjectionDigest(RecapGridHash.Compute(
            "atelia.recap-grid.prior-projection.v1",
            RecapGridCanonical.Encode(new PriorInputProjectionBodyDto(1, content))
        ));
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
