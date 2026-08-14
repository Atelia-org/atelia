using System.Collections.Immutable;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal static class RuntimeProtocolValidator {
    internal static RuntimePreflightResult.Rejected? Validate(
        FamilyDefinition family
    ) {
        FamilyToolDefinition? terminal = family.OrderedTools.Count == 1
            ? family.OrderedTools[0]
            : null;
        if (terminal is null
            || !string.Equals(
                terminal.Name,
                RecapRewriterProtocolV2.TerminalToolName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                family.OutputProtocol.TerminalToolName,
                RecapRewriterProtocolV2.TerminalToolName,
                StringComparison.Ordinal
            )
            || family.OutputProtocol.ToolChoice != FamilyToolChoice.Required
            || family.OutputProtocol.AllowParallel is not false
            || terminal.InputSchema.Nullable
            || terminal.InputSchema.Description is not null
            || terminal.InputSchema.Properties.Count != 2
            || terminal.InputSchema.Properties[0] is not {
                Name: "outcome",
                Required: true,
                Schema: FamilyScalarInputSchema outcome
            }
            || outcome.Nullable
            || outcome.Description is not null
            || outcome.ScalarType != FamilyScalarType.String
            || !outcome.OrderedEnum.SequenceEqual([
                RecapRewriterProtocolV2.UpdatedOutcome,
                RecapRewriterProtocolV2.KeepUnchangedOutcome
            ], StringComparer.Ordinal)
            || terminal.InputSchema.Properties[1] is not {
                Name: "content",
                Required: true,
                Schema: FamilyScalarInputSchema content
            }
            || !content.Nullable
            || content.Description is not null
            || content.ScalarType != FamilyScalarType.String
            || content.OrderedEnum.Count != 0) {
            return new RuntimePreflightResult.Rejected(
                "OutputProtocolMismatch",
                "Runtime V2 requires exactly one reserved terminal tool, Required choice, disabled parallel calls, and the exact outcome/content schema."
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
        return new CompletionOutputContract(
            tools,
            CompletionToolChoice.RequiredNamed(
                RecapRewriterProtocolV2.TerminalToolName
            ),
            allowParallelToolCalls: false
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
