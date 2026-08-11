using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal static class RuntimeProtocolValidator {
    internal static RuntimePreflightResult.Rejected? Validate(
        FamilyDefinition family
    ) {
        FamilyToolDefinition? terminal = family.OrderedTools.SingleOrDefault(
            tool => string.Equals(
                tool.Name,
                family.OutputProtocol.TerminalToolName,
                StringComparison.Ordinal
            )
        );
        if (terminal is null
            || terminal.InputSchema.Nullable
            || terminal.InputSchema.Properties.Count != 2
            || terminal.InputSchema.Properties[0] is not {
                Name: "outcome",
                Required: true,
                Schema: FamilyScalarInputSchema outcome
            }
            || outcome.Nullable
            || outcome.ScalarType != FamilyScalarType.String
            || !outcome.OrderedEnum.SequenceEqual([
                RecapCompletionProtocolV1.UpdatedOutcome,
                RecapCompletionProtocolV1.KeepUnchangedOutcome
            ], StringComparer.Ordinal)
            || terminal.InputSchema.Properties[1] is not {
                Name: "content",
                Required: true,
                Schema: FamilyScalarInputSchema content
            }
            || !content.Nullable
            || content.ScalarType != FamilyScalarType.String
            || content.OrderedEnum.Count != 0) {
            return new RuntimePreflightResult.Rejected(
                "OutputProtocolMismatch",
                "The terminal tool must use the exact V1 outcome/content schema."
            );
        }
        return null;
    }

    internal static CompletionOutputContract CreateOutputContract(
        FamilyDefinition family
    ) {
        ImmutableArray<ToolDefinition> tools = [.. family.OrderedTools
            .Select(static tool => new ToolDefinition(
                tool.Name,
                tool.Description,
                (ToolSchema.Object)ConvertSchema(tool.InputSchema)
            ))];
        CompletionToolChoice choice = family.OutputProtocol.ToolChoice switch {
            FamilyToolChoice.Auto => CompletionToolChoice.Auto,
            FamilyToolChoice.Required => CompletionToolChoice.RequiredNamed(
                family.OutputProtocol.TerminalToolName
            ),
            _ => throw new InvalidOperationException(
                "The family tool choice is unsupported."
            )
        };
        return new CompletionOutputContract(
            tools,
            choice,
            family.OutputProtocol.AllowParallel
        );
    }

    private static ToolSchema ConvertSchema(FamilyInputSchema value)
        => value switch {
            FamilyObjectInputSchema item => new ToolSchema.Object(
                [.. item.Properties.Select(static property =>
                    new ToolSchema.Property(
                        property.Name,
                        ConvertSchema(property.Schema),
                        property.Required
                    ))],
                additionalProperties: false,
                description: item.Description,
                isNullable: item.Nullable
            ),
            FamilyArrayInputSchema item => new ToolSchema.Array(
                ConvertSchema(item.Item),
                item.Nullable,
                item.Description
            ),
            FamilyScalarInputSchema item => new ToolSchema.Value(
                item.ScalarType switch {
                    FamilyScalarType.String => ToolParamType.String,
                    FamilyScalarType.Boolean => ToolParamType.Boolean,
                    FamilyScalarType.Int64 => ToolParamType.Int64,
                    _ => throw new InvalidOperationException(
                        "The family scalar type is unsupported."
                    )
                },
                item.Nullable,
                description: item.Description,
                stringEnumValues: item.OrderedEnum
            ),
            _ => throw new InvalidOperationException(
                "The family input schema subtype is unsupported."
            )
        };
}
