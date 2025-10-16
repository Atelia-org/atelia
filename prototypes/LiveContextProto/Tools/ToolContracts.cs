using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal interface ITool {
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ToolParameter> Parameters { get; }
    ValueTask<ToolHandlerResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken);
}

internal sealed record ToolExecutionContext {
    public ToolCallRequest Request { get; }
    public ImmutableDictionary<string, object?> Environment { get; }

    public ToolExecutionContext(ToolCallRequest request, ImmutableDictionary<string, object?>? environment = null) {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Environment = environment ?? ImmutableDictionary<string, object?>.Empty;
    }
}

internal sealed class ToolParameter {
    public ToolParameter(
        string name,
        ToolParameterValueKind valueKind,
        ToolParameterCardinality cardinality,
        bool isRequired,
        string description,
        ToolParameterEnumConstraint? enumConstraint = null,
        string? example = null
    ) {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Parameter name cannot be empty.", nameof(name))
            : name;
        ValueKind = valueKind;
        Cardinality = cardinality;
        IsRequired = isRequired;
        Description = string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("Parameter description cannot be empty.", nameof(description))
            : description;
        EnumConstraint = enumConstraint;
        Example = example;
    }

    public string Name { get; }

    public ToolParameterValueKind ValueKind { get; }

    public ToolParameterCardinality Cardinality { get; }

    public bool IsRequired { get; }

    public string Description { get; }

    public ToolParameterEnumConstraint? EnumConstraint { get; }

    public string? Example { get; }
}

internal enum ToolParameterValueKind {
    String,
    Boolean,
    Integer,
    Number,
    JsonObject,
    JsonArray,
    Timestamp,
    Uri,
    EnumToken,
    AttachmentReference
}

internal enum ToolParameterCardinality {
    Single,
    Optional,
    List,
    Map
}

internal sealed class ToolParameterEnumConstraint {
    public ToolParameterEnumConstraint(IReadOnlyList<string> allowedValues, bool caseSensitive = false) {
        if (allowedValues is null) { throw new ArgumentNullException(nameof(allowedValues)); }
        if (allowedValues.Count == 0) { throw new ArgumentException("At least one allowed value must be provided.", nameof(allowedValues)); }

        AllowedValues = allowedValues;
        CaseSensitive = caseSensitive;
    }

    public IReadOnlyList<string> AllowedValues { get; }

    public bool CaseSensitive { get; }

    public bool Contains(string value) {
        if (value is null) { return false; }

        if (CaseSensitive) {
            foreach (var allowed in AllowedValues) {
                if (string.Equals(allowed, value, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        foreach (var allowed in AllowedValues) {
            if (string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase)) { return true; }
        }

        return false;
    }
}
