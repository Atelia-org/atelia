using System.Collections.ObjectModel;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.RecapGrid;

public enum FamilyScalarType {
    String,
    Boolean,
    Int64
}

public enum FamilyOutputMode {
    FullReplacementText
}

public abstract class FamilyInputSchema {
    private protected FamilyInputSchema(
        bool nullable,
        string? description
    ) {
        Nullable = nullable;
        Description = description is null
            ? null
            : RecapGridSyntax.RequireText(
                description,
                RecapGridLimits.MaximumToolDescriptionUtf8Bytes,
                nameof(description),
                allowEmpty: true
            );
    }

    public bool Nullable { get; }
    public string? Description { get; }

    internal abstract FamilyInputSchemaDto ToDto();
}

public sealed class FamilyObjectInputSchema : FamilyInputSchema {
    private readonly ReadOnlyCollection<FamilyToolProperty> _properties;

    public FamilyObjectInputSchema(
        IEnumerable<FamilyToolProperty> properties,
        bool nullable = false,
        string? description = null
    ) : base(nullable, description) {
        FamilyToolProperty[] copy = RecapGridSyntax.MaterializeBounded(
            properties,
            RecapGridLimits.MaximumObjectPropertyCount,
            nameof(properties)
        );
        if (copy.Any(static property => property is null)) {
            throw new ArgumentException(
                "Object properties must not contain null.",
                nameof(properties)
            );
        }
        if (copy.Select(static property => property.Name)
            .Distinct(StringComparer.Ordinal).Count() != copy.Length) {
            throw new ArgumentException(
                "Object property names must be unique.",
                nameof(properties)
            );
        }
        _properties = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<FamilyToolProperty> Properties => _properties;

    internal override FamilyInputSchemaDto ToDto() => new(
        "object",
        Nullable,
        Description,
        null,
        _properties.Select(static item => item.ToDto()).ToArray(),
        null,
        Array.Empty<string>()
    );
}

public sealed class FamilyArrayInputSchema : FamilyInputSchema {
    public FamilyArrayInputSchema(
        FamilyInputSchema item,
        bool nullable = false,
        string? description = null
    ) : base(nullable, description) {
        Item = item ?? throw new ArgumentNullException(nameof(item));
    }

    public FamilyInputSchema Item { get; }

    internal override FamilyInputSchemaDto ToDto() => new(
        "array",
        Nullable,
        Description,
        null,
        Array.Empty<FamilyToolPropertyDto>(),
        Item.ToDto(),
        Array.Empty<string>()
    );
}

public sealed class FamilyScalarInputSchema : FamilyInputSchema {
    private readonly ReadOnlyCollection<string> _orderedEnum;

    public FamilyScalarInputSchema(
        FamilyScalarType scalarType,
        bool nullable = false,
        string? description = null,
        IEnumerable<string>? orderedEnum = null
    ) : base(nullable, description) {
        ScalarType = scalarType;
        string[] copy = orderedEnum is null
            ? Array.Empty<string>()
            : RecapGridSyntax.MaterializeBounded(
                orderedEnum,
                RecapGridLimits.MaximumObjectPropertyCount,
                nameof(orderedEnum)
            ).Select(static value => RecapGridSyntax.RequireText(
                value,
                RecapGridLimits.MaximumIdentifierUtf8Bytes,
                nameof(orderedEnum)
            )).ToArray();
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length) {
            throw new ArgumentException(
                "Scalar enum members must be unique.",
                nameof(orderedEnum)
            );
        }
        if (copy.Length != 0 && scalarType != FamilyScalarType.String) {
            throw new ArgumentException(
                "Only string schemas may declare an ordered enum in V1.",
                nameof(orderedEnum)
            );
        }
        _orderedEnum = Array.AsReadOnly(copy);
    }

    public FamilyScalarType ScalarType { get; }
    public IReadOnlyList<string> OrderedEnum => _orderedEnum;

    internal override FamilyInputSchemaDto ToDto() => new(
        "scalar",
        Nullable,
        Description,
        ScalarType switch {
            FamilyScalarType.String => "string",
            FamilyScalarType.Boolean => "boolean",
            FamilyScalarType.Int64 => "int64",
            _ => throw new InvalidOperationException(
                "The scalar type is not supported."
            )
        },
        Array.Empty<FamilyToolPropertyDto>(),
        null,
        _orderedEnum.ToArray()
    );
}

public sealed class FamilyToolProperty {
    public FamilyToolProperty(
        string name,
        FamilyInputSchema schema,
        bool required
    ) {
        Name = RecapGridSyntax.RequireIdentifier(name, nameof(name));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Required = required;
    }

    public string Name { get; }
    public FamilyInputSchema Schema { get; }
    public bool Required { get; }

    internal FamilyToolPropertyDto ToDto()
        => new(Name, Schema.ToDto(), Required);
}

public sealed class FamilyToolDefinition {
    public FamilyToolDefinition(
        string name,
        string description,
        FamilyObjectInputSchema inputSchema
    ) {
        Name = RecapGridSyntax.RequireIdentifier(name, nameof(name));
        Description = RecapGridSyntax.RequireText(
            description,
            RecapGridLimits.MaximumToolDescriptionUtf8Bytes,
            nameof(description)
        );
        InputSchema = inputSchema
            ?? throw new ArgumentNullException(nameof(inputSchema));
        if (inputSchema.Nullable) {
            throw new ArgumentException(
                "A V1 tool root input object must be non-nullable.",
                nameof(inputSchema)
            );
        }
    }

    public string Name { get; }
    public string Description { get; }
    public FamilyObjectInputSchema InputSchema { get; }

    internal FamilyToolDefinitionDto ToDto()
        => new(Name, Description, InputSchema.ToDto());
}

public sealed class FamilyOutputProtocol {
    public FamilyOutputProtocol(
        string protocolId,
        FamilyOutputMode mode
    ) {
        ProtocolId = RecapGridSyntax.RequireIdentifier(
            protocolId,
            nameof(protocolId)
        );
        if (!Enum.IsDefined(mode)) {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        Mode = mode;
    }

    public string ProtocolId { get; }
    public FamilyOutputMode Mode { get; }
}

public sealed class FamilyInputRenderingProtocol {
    public FamilyInputRenderingProtocol(
        string protocolId,
        string priorProjectionSchemaId,
        string historySegmentRenderingSchemaId
    ) {
        ProtocolId = RecapGridSyntax.RequireIdentifier(
            protocolId,
            nameof(protocolId)
        );
        PriorProjectionSchemaId = RecapGridSyntax.RequireIdentifier(
            priorProjectionSchemaId,
            nameof(priorProjectionSchemaId)
        );
        HistorySegmentRenderingSchemaId = RecapGridSyntax.RequireIdentifier(
            historySegmentRenderingSchemaId,
            nameof(historySegmentRenderingSchemaId)
        );
    }

    public string ProtocolId { get; }
    public string PriorProjectionSchemaId { get; }
    public string HistorySegmentRenderingSchemaId { get; }
}

public sealed class FamilyDefinition {
    private readonly ReadOnlyCollection<FamilyToolDefinition> _orderedTools;
    private readonly byte[] _canonicalBytes;

    private FamilyDefinition(
        string systemPrompt,
        FamilyToolDefinition[] orderedTools,
        FamilyOutputProtocol outputProtocol,
        FamilyInputRenderingProtocol inputRenderingProtocol,
        FamilyDefinitionDigest digest,
        byte[] canonicalBytes
    ) {
        SystemPrompt = systemPrompt;
        _orderedTools = Array.AsReadOnly(orderedTools);
        OutputProtocol = outputProtocol;
        InputRenderingProtocol = inputRenderingProtocol;
        Digest = digest;
        _canonicalBytes = canonicalBytes;
    }

    public string SystemPrompt { get; }

    /// <summary>
    /// The canonical ordered tool declarations for this Family. A runtime may
    /// execute only a protocol shape it validates exactly; declaring additional
    /// tools does not grant dispatch capability.
    /// </summary>
    public IReadOnlyList<FamilyToolDefinition> OrderedTools => _orderedTools;
    public FamilyOutputProtocol OutputProtocol { get; }
    public FamilyInputRenderingProtocol InputRenderingProtocol { get; }
    public FamilyDefinitionDigest Digest { get; }

    public static FamilyDefinition Create(
        string systemPrompt,
        IEnumerable<FamilyToolDefinition> orderedTools,
        FamilyOutputProtocol outputProtocol,
        FamilyInputRenderingProtocol inputRenderingProtocol
    ) {
        systemPrompt = RecapGridSyntax.RequireText(
            systemPrompt,
            RecapGridLimits.MaximumSystemPromptUtf8Bytes,
            nameof(systemPrompt)
        );
        ArgumentNullException.ThrowIfNull(orderedTools);
        ArgumentNullException.ThrowIfNull(outputProtocol);
        ArgumentNullException.ThrowIfNull(inputRenderingProtocol);
        FamilyToolDefinition[] tools = RecapGridSyntax.MaterializeBounded(
            orderedTools,
            RecapGridLimits.MaximumToolCount,
            nameof(orderedTools)
        );
        if (tools.Length > RecapGridLimits.MaximumToolCount) {
            throw new ArgumentOutOfRangeException(nameof(orderedTools));
        }
        if (tools.Any(static tool => tool is null)
            || tools.Select(static tool => tool.Name)
                .Distinct(StringComparer.Ordinal).Count() != tools.Length) {
            throw new ArgumentException(
                "Tools must be non-null and have unique names.",
                nameof(orderedTools)
            );
        }
        if (outputProtocol.Mode is FamilyOutputMode.FullReplacementText
            && tools.Length != 0) {
            throw new ArgumentException(
                "FullReplacementText output requires an empty OrderedTools set.",
                nameof(orderedTools)
            );
        }
        ValidateSchemas(tools);
        FamilyDefinitionBodyDto body = BodyDto(
            systemPrompt,
            tools,
            outputProtocol,
            inputRenderingProtocol
        );
        FamilyDefinitionDigest digest = new(RecapGridHash.Compute(
            "atelia.recap-grid.family-definition.v2",
            RecapGridCanonical.Encode(body)
        ));
        byte[] canonical = RecapGridCanonical.Encode(new FamilyDefinitionDto(
            2,
            digest.Value,
            body.SystemPrompt,
            body.OrderedTools,
            body.OutputProtocol,
            body.InputRenderingProtocol
        ));
        if (canonical.Length > RecapGridLimits.MaximumFamilyCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(systemPrompt));
        }
        return new FamilyDefinition(
            systemPrompt,
            tools,
            outputProtocol,
            inputRenderingProtocol,
            digest,
            canonical
        );
    }

    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public static FamilyDefinition DecodeCanonical(ReadOnlySpan<byte> bytes) {
        try {
            return DecodeCanonicalCore(bytes);
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NullReferenceException) {
            throw new InvalidDataException(
                "The family canonical value is invalid.",
                exception
            );
        }
    }

    private static FamilyDefinition DecodeCanonicalCore(
        ReadOnlySpan<byte> bytes
    ) {
        FamilyDefinitionDto dto = RecapGridCanonical.DecodeExact<FamilyDefinitionDto>(
            bytes,
            RecapGridLimits.MaximumFamilyCanonicalUtf8Bytes,
            nameof(bytes)
        );
        if (dto.SchemaVersion != 2
            || dto.OrderedTools is null
            || dto.OutputProtocol is null
            || dto.InputRenderingProtocol is null) {
            throw new InvalidDataException(
                "The family schema version or required fields are invalid."
            );
        }
        FamilyDefinition value = Create(
            dto.SystemPrompt,
            dto.OrderedTools.Select(FromDto),
            new FamilyOutputProtocol(
                dto.OutputProtocol.ProtocolId,
                ParseOutputMode(dto.OutputProtocol.Mode)
            ),
            new FamilyInputRenderingProtocol(
                dto.InputRenderingProtocol.ProtocolId,
                dto.InputRenderingProtocol.PriorProjectionSchemaId,
                dto.InputRenderingProtocol.HistorySegmentRenderingSchemaId
            )
        );
        if (!string.Equals(value.Digest.Value, dto.Digest, StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The family digest does not match its canonical body.",
                nameof(bytes)
            );
        }
        return value;
    }

    private static FamilyDefinitionBodyDto BodyDto(
        string systemPrompt,
        FamilyToolDefinition[] tools,
        FamilyOutputProtocol output,
        FamilyInputRenderingProtocol input
    ) => new(
        2,
        systemPrompt,
        tools.Select(static item => item.ToDto()).ToArray(),
        new FamilyOutputProtocolDto(
            output.ProtocolId,
            output.Mode switch {
                FamilyOutputMode.FullReplacementText =>
                    "full-replacement-text",
                _ => throw new InvalidOperationException(
                    "The output mode is unsupported."
                )
            }
        ),
        new FamilyInputRenderingProtocolDto(
            input.ProtocolId,
            input.PriorProjectionSchemaId,
            input.HistorySegmentRenderingSchemaId
        )
    );

    private static FamilyToolDefinition FromDto(FamilyToolDefinitionDto dto) {
        FamilyInputSchema schema = FromDto(dto.InputSchema);
        if (schema is not FamilyObjectInputSchema objectSchema) {
            throw new ArgumentException(
                "Tool input schemas must be objects.",
                nameof(dto)
            );
        }
        return new FamilyToolDefinition(dto.Name, dto.Description, objectSchema);
    }

    private static FamilyInputSchema FromDto(FamilyInputSchemaDto dto)
        => dto.Kind switch {
            "object" when dto.ScalarType is null
                && dto.Item is null
                && dto.OrderedEnum.Length == 0
                => new FamilyObjectInputSchema(
                    dto.Properties.Select(static property =>
                        new FamilyToolProperty(
                            property.Name,
                            FromDto(property.Schema),
                            property.Required
                        )),
                    dto.Nullable,
                    dto.Description
                ),
            "array" when dto.ScalarType is null
                && dto.Properties.Length == 0
                && dto.Item is not null
                && dto.OrderedEnum.Length == 0
                => new FamilyArrayInputSchema(
                    FromDto(dto.Item),
                    dto.Nullable,
                    dto.Description
                ),
            "scalar" when dto.ScalarType is not null
                && dto.Properties.Length == 0
                && dto.Item is null
                => new FamilyScalarInputSchema(
                    ParseScalarType(dto.ScalarType),
                    dto.Nullable,
                    dto.Description,
                    dto.OrderedEnum
                ),
            _ => throw new ArgumentException(
                "The input schema discriminant or fields are invalid.",
                nameof(dto)
            )
        };

    private static FamilyScalarType ParseScalarType(string value) => value switch {
        "string" => FamilyScalarType.String,
        "boolean" => FamilyScalarType.Boolean,
        "int64" => FamilyScalarType.Int64,
        _ => throw new ArgumentException("The scalar type is unsupported.")
    };

    private static FamilyOutputMode ParseOutputMode(string value) => value switch {
        "full-replacement-text" => FamilyOutputMode.FullReplacementText,
        _ => throw new ArgumentException("The output mode is unsupported.")
    };

    private static void ValidateSchemas(IEnumerable<FamilyToolDefinition> tools) {
        int nodes = 0;
        foreach (FamilyToolDefinition tool in tools) {
            Visit(tool.InputSchema, 1);
        }
        return;

        void Visit(FamilyInputSchema schema, int depth) {
            if (depth > RecapGridLimits.MaximumToolSchemaDepth
                || ++nodes > RecapGridLimits.MaximumToolSchemaNodeCount) {
                throw new ArgumentOutOfRangeException(nameof(tools));
            }
            switch (schema) {
                case FamilyObjectInputSchema value:
                    foreach (FamilyToolProperty property in value.Properties) {
                        Visit(property.Schema, depth + 1);
                    }
                    break;
                case FamilyArrayInputSchema value:
                    Visit(value.Item, depth + 1);
                    break;
                case FamilyScalarInputSchema:
                    break;
                default:
                    throw new ArgumentException(
                        "The input schema subtype is unsupported.",
                        nameof(tools)
                    );
            }
        }
    }
}

public enum MaintainerReadableScope {
    FullPriorBuildTargetAndCurrentHistorySegmentV1
}

public sealed class MaintainerCapabilitySpec {
    public MaintainerCapabilitySpec(
        string runtimeProtocolId,
        MaintainerReadableScope readableScope,
        string? semanticModelId = null
    ) {
        RuntimeProtocolId = RecapGridSyntax.RequireIdentifier(
            runtimeProtocolId,
            nameof(runtimeProtocolId)
        );
        SemanticModelId = semanticModelId is null
            ? null
            : RecapGridSyntax.RequireIdentifier(
                semanticModelId,
                nameof(semanticModelId)
            );
        if (readableScope
            != MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1) {
            throw new ArgumentOutOfRangeException(nameof(readableScope));
        }
        ReadableScope = readableScope;
        CapabilityFingerprint = RecapGridHash.Compute(
            "atelia.recap-grid.capability.v1",
            RecapGridCanonical.Encode(new MaintainerCapabilityDto(
                1,
                RuntimeProtocolId,
                ReadableScopeToken(ReadableScope),
                SemanticModelId
            ))
        );
    }

    public string RuntimeProtocolId { get; }
    public MaintainerReadableScope ReadableScope { get; }
    public string? SemanticModelId { get; }
    public string CapabilityFingerprint { get; }

    private static string ReadableScopeToken(
        MaintainerReadableScope value
    ) => value switch {
        MaintainerReadableScope
            .FullPriorBuildTargetAndCurrentHistorySegmentV1
            => "full-prior-build-target-and-current-history-segment-v1",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    internal static MaintainerReadableScope ParseReadableScope(
        string value
    ) => value switch {
        "full-prior-build-target-and-current-history-segment-v1"
            => MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1,
        _ => throw new ArgumentException(
            "The readable scope is unsupported.",
            nameof(value)
        )
    };

    internal string ReadableScopeStorageToken()
        => ReadableScopeToken(ReadableScope);
}

public sealed class MaintainerDeclarativeSpec {
    public MaintainerDeclarativeSpec(
        string topic,
        string userPromptTemplate
    ) {
        Topic = RecapGridSyntax.RequireText(
            topic,
            RecapGridLimits.MaximumTopicUtf8Bytes,
            nameof(topic)
        );
        UserPromptTemplate = RecapGridSyntax.RequireText(
            userPromptTemplate,
            RecapGridLimits.MaximumUserPromptUtf8Bytes,
            nameof(userPromptTemplate)
        );
    }

    public string Topic { get; }
    public string UserPromptTemplate { get; }
}

public sealed class MaintainerDefinitionRevision {
    private readonly byte[] _canonicalBytes;

    private MaintainerDefinitionRevision(
        LogicalColumnId logicalColumnId,
        FamilyDefinitionDigest familyDigest,
        ContextHeaderBlockTarget target,
        MaintainerCapabilitySpec capability,
        MaintainerDeclarativeSpec declarativeSpec,
        int maxContentUtf8Bytes,
        MaintainerDefinitionDigest digest,
        byte[] canonicalBytes
    ) {
        LogicalColumnId = logicalColumnId;
        FamilyDigest = familyDigest;
        Target = target;
        Capability = capability;
        DeclarativeSpec = declarativeSpec;
        MaxContentUtf8Bytes = maxContentUtf8Bytes;
        Digest = digest;
        _canonicalBytes = canonicalBytes;
    }

    public LogicalColumnId LogicalColumnId { get; }
    public FamilyDefinitionDigest FamilyDigest { get; }
    public ContextHeaderBlockTarget Target { get; }
    public MaintainerCapabilitySpec Capability { get; }
    public MaintainerDeclarativeSpec DeclarativeSpec { get; }
    public int MaxContentUtf8Bytes { get; }
    public MaintainerDefinitionDigest Digest { get; }

    public static MaintainerDefinitionRevision Create(
        LogicalColumnId logicalColumnId,
        FamilyDefinitionDigest familyDigest,
        ContextHeaderBlockTarget target,
        MaintainerCapabilitySpec capability,
        MaintainerDeclarativeSpec declarativeSpec,
        int maxContentUtf8Bytes
    ) {
        RecapGridSyntax.RequireIdentifier(
            logicalColumnId.Value
                ?? throw new ArgumentException(
                    "LogicalColumnId must not be default.",
                    nameof(logicalColumnId)
                ),
            nameof(logicalColumnId)
        );
        RecapGridSyntax.RequireTypedValue(
            familyDigest.Value,
            64,
            nameof(familyDigest)
        );
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(declarativeSpec);
        if (maxContentUtf8Bytes is < 1
            or > RecapGridLimits.MaximumContentUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(maxContentUtf8Bytes));
        }
        MaintainerDefinitionBodyDto body = new(
            2,
            logicalColumnId.Value,
            familyDigest.Value,
            new ContextHeaderTargetDto(
                ContextHeaderCarrierTokens.ToStorageToken(target.Carrier),
                RecapGridSyntax.RequireIdentifier(
                    target.BlockKey,
                    nameof(target)
                ),
                target.SemanticHeading
            ),
            capability.CapabilityFingerprint,
            new MaintainerDeclarativeDto(
                declarativeSpec.Topic,
                declarativeSpec.UserPromptTemplate
            ),
            maxContentUtf8Bytes
        );
        MaintainerDefinitionDigest digest = new(RecapGridHash.Compute(
            "atelia.recap-grid.maintainer-definition.v2",
            RecapGridCanonical.Encode(body)
        ));
        byte[] canonical = RecapGridCanonical.Encode(
            new MaintainerDefinitionDto(
                2,
                digest.Value,
                body.LogicalColumnId,
                body.FamilyDigest,
                body.Target,
                new MaintainerCapabilityDto(
                    1,
                    capability.RuntimeProtocolId,
                    capability.ReadableScopeStorageToken(),
                    capability.SemanticModelId
                ),
                body.CapabilityFingerprint,
                body.DeclarativeSpec,
                body.MaxContentUtf8Bytes
            )
        );
        if (canonical.Length
            > RecapGridLimits.MaximumDefinitionCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(declarativeSpec));
        }
        return new MaintainerDefinitionRevision(
            logicalColumnId,
            familyDigest,
            new ContextHeaderBlockTarget(
                target.Carrier,
                target.BlockKey,
                target.SemanticHeading
            ),
            capability,
            declarativeSpec,
            maxContentUtf8Bytes,
            digest,
            canonical
        );
    }

    public byte[] ToCanonicalBytes() => (byte[])_canonicalBytes.Clone();

    public static MaintainerDefinitionRevision DecodeCanonical(
        ReadOnlySpan<byte> bytes
    ) {
        try {
            return DecodeCanonicalCore(bytes);
        }
        catch (InvalidDataException) {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or NullReferenceException
            or System.Text.Json.JsonException) {
            throw new InvalidDataException(
                "The maintainer definition canonical value is invalid.",
                exception
            );
        }
    }

    private static MaintainerDefinitionRevision DecodeCanonicalCore(
        ReadOnlySpan<byte> bytes
    ) {
        int schemaVersion = ReadSchemaVersion(bytes);
        return schemaVersion switch {
            1 => DecodeCanonicalV1(bytes),
            2 => DecodeCanonicalV2(bytes),
            _ => throw new InvalidDataException(
                "The maintainer definition schema version is unsupported."
            )
        };
    }

    private static MaintainerDefinitionRevision DecodeCanonicalV2(
        ReadOnlySpan<byte> bytes
    ) {
        MaintainerDefinitionDto dto = RecapGridCanonical
            .DecodeExact<MaintainerDefinitionDto>(
                bytes,
                RecapGridLimits.MaximumDefinitionCanonicalUtf8Bytes,
                nameof(bytes)
            );
        if (dto.SchemaVersion != 2
            || dto.Target is null
            || dto.Capability is null
            || dto.Capability.SchemaVersion != 1
            || dto.DeclarativeSpec is null
            || !ContextHeaderCarrierTokens.TryParseStorageToken(
                dto.Target.Carrier,
                out ContextHeaderCarrier carrier)) {
            throw new InvalidDataException(
                "The context-header carrier is unsupported.",
                new ArgumentException(nameof(bytes))
            );
        }
        MaintainerDefinitionRevision value = Create(
            new LogicalColumnId(dto.LogicalColumnId),
            new FamilyDefinitionDigest(dto.FamilyDigest),
            new ContextHeaderBlockTarget(
                carrier,
                dto.Target.BlockKey,
                dto.Target.SemanticHeading
            ),
            new MaintainerCapabilitySpec(
                dto.Capability.RuntimeProtocolId,
                MaintainerCapabilitySpec.ParseReadableScope(
                    dto.Capability.ReadableScope
                ),
                dto.Capability.SemanticModelId
            ),
            new MaintainerDeclarativeSpec(
                dto.DeclarativeSpec.Topic,
                dto.DeclarativeSpec.UserPromptTemplate
            ),
            dto.MaxContentUtf8Bytes
        );
        if (!string.Equals(
                value.Capability.CapabilityFingerprint,
                dto.CapabilityFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(value.Digest.Value, dto.Digest, StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The maintainer definition digest does not match its body.",
                nameof(bytes)
            );
        }
        return value;
    }

    private static MaintainerDefinitionRevision DecodeCanonicalV1(
        ReadOnlySpan<byte> bytes
    ) {
        MaintainerDefinitionV1Dto dto = RecapGridCanonical
            .DecodeExact<MaintainerDefinitionV1Dto>(
                bytes,
                RecapGridLimits.MaximumDefinitionCanonicalUtf8Bytes,
                nameof(bytes)
            );
        if (dto.SchemaVersion != 1
            || dto.Target is null
            || dto.Capability is null
            || dto.Capability.SchemaVersion != 1
            || dto.DeclarativeSpec is null
            || !ContextHeaderCarrierTokens.TryParseStorageToken(
                dto.Target.Carrier,
                out ContextHeaderCarrier carrier)) {
            throw new InvalidDataException(
                "The context-header carrier is unsupported.",
                new ArgumentException(nameof(bytes))
            );
        }
        LogicalColumnId logicalColumnId = new(dto.LogicalColumnId);
        FamilyDefinitionDigest familyDigest = new(dto.FamilyDigest);
        string blockKey = RecapGridSyntax.RequireIdentifier(
            dto.Target.BlockKey,
            nameof(bytes)
        );
        var capability = new MaintainerCapabilitySpec(
            dto.Capability.RuntimeProtocolId,
            MaintainerCapabilitySpec.ParseReadableScope(
                dto.Capability.ReadableScope
            ),
            dto.Capability.SemanticModelId
        );
        var declarativeSpec = new MaintainerDeclarativeSpec(
            dto.DeclarativeSpec.Topic,
            dto.DeclarativeSpec.UserPromptTemplate
        );
        if (dto.MaxContentUtf8Bytes is < 1
            or > RecapGridLimits.MaximumContentUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }
        var body = new MaintainerDefinitionBodyV1Dto(
            1,
            logicalColumnId.Value,
            familyDigest.Value,
            new ContextHeaderTargetV1Dto(
                ContextHeaderCarrierTokens.ToStorageToken(carrier),
                blockKey
            ),
            capability.CapabilityFingerprint,
            new MaintainerDeclarativeDto(
                declarativeSpec.Topic,
                declarativeSpec.UserPromptTemplate
            ),
            dto.MaxContentUtf8Bytes
        );
        MaintainerDefinitionDigest digest = new(RecapGridHash.Compute(
            "atelia.recap-grid.maintainer-definition.v1",
            RecapGridCanonical.Encode(body)
        ));
        if (!string.Equals(
                capability.CapabilityFingerprint,
                dto.CapabilityFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(digest.Value, dto.Digest, StringComparison.Ordinal)) {
            throw new ArgumentException(
                "The maintainer definition digest does not match its body.",
                nameof(bytes)
            );
        }
        return new MaintainerDefinitionRevision(
            logicalColumnId,
            familyDigest,
            new ContextHeaderBlockTarget(
                carrier,
                blockKey,
                LegacySemanticHeading(carrier, blockKey)
            ),
            capability,
            declarativeSpec,
            dto.MaxContentUtf8Bytes,
            digest,
            bytes.ToArray()
        );
    }

    private static int ReadSchemaVersion(ReadOnlySpan<byte> bytes) {
        if (bytes.Length is < 2
            || bytes.Length
                > RecapGridLimits.MaximumDefinitionCanonicalUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }
        var reader = new System.Text.Json.Utf8JsonReader(bytes);
        if (!reader.Read()
            || reader.TokenType
                != System.Text.Json.JsonTokenType.StartObject
            || !reader.Read()
            || reader.TokenType
                != System.Text.Json.JsonTokenType.PropertyName
            || !reader.ValueTextEquals("schemaVersion")
            || !reader.Read()
            || reader.TokenType
                != System.Text.Json.JsonTokenType.Number
            || !reader.TryGetInt32(out int schemaVersion)) {
            throw new ArgumentException(
                "The maintainer definition canonical value must begin with schemaVersion.",
                nameof(bytes)
            );
        }
        return schemaVersion;
    }

    private static string LegacySemanticHeading(
        ContextHeaderCarrier carrier,
        string blockKey
    ) => carrier switch {
        ContextHeaderCarrier.System =>
            $"Derived context from prior history: {blockKey}",
        ContextHeaderCarrier.Observation =>
            $"Derived context from prior history, not a new user request: {blockKey}",
        ContextHeaderCarrier.Action =>
            $"Derived context from prior history, not the current Assistant reply: {blockKey}",
        _ => throw new ArgumentOutOfRangeException(nameof(carrier))
    };
}

internal sealed record FamilyToolPropertyDto(
    string Name,
    FamilyInputSchemaDto Schema,
    bool Required
);

internal sealed record FamilyInputSchemaDto(
    string Kind,
    bool Nullable,
    string? Description,
    string? ScalarType,
    FamilyToolPropertyDto[] Properties,
    FamilyInputSchemaDto? Item,
    string[] OrderedEnum
);

internal sealed record FamilyToolDefinitionDto(
    string Name,
    string Description,
    FamilyInputSchemaDto InputSchema
);

internal sealed record FamilyOutputProtocolDto(
    string ProtocolId,
    string Mode
);

internal sealed record FamilyInputRenderingProtocolDto(
    string ProtocolId,
    string PriorProjectionSchemaId,
    string HistorySegmentRenderingSchemaId
);

internal sealed record FamilyDefinitionBodyDto(
    int SchemaVersion,
    string SystemPrompt,
    FamilyToolDefinitionDto[] OrderedTools,
    FamilyOutputProtocolDto OutputProtocol,
    FamilyInputRenderingProtocolDto InputRenderingProtocol
);

internal sealed record FamilyDefinitionDto(
    int SchemaVersion,
    string Digest,
    string SystemPrompt,
    FamilyToolDefinitionDto[] OrderedTools,
    FamilyOutputProtocolDto OutputProtocol,
    FamilyInputRenderingProtocolDto InputRenderingProtocol
);

internal sealed record ContextHeaderTargetDto(
    string Carrier,
    string BlockKey,
    string SemanticHeading
);

internal sealed record ContextHeaderTargetV1Dto(
    string Carrier,
    string BlockKey
);

internal sealed record MaintainerCapabilityDto(
    int SchemaVersion,
    string RuntimeProtocolId,
    string ReadableScope,
    string? SemanticModelId
);

internal sealed record MaintainerDeclarativeDto(
    string Topic,
    string UserPromptTemplate
);

internal sealed record MaintainerDefinitionBodyDto(
    int SchemaVersion,
    string LogicalColumnId,
    string FamilyDigest,
    ContextHeaderTargetDto Target,
    string CapabilityFingerprint,
    MaintainerDeclarativeDto DeclarativeSpec,
    int MaxContentUtf8Bytes
);

internal sealed record MaintainerDefinitionDto(
    int SchemaVersion,
    string Digest,
    string LogicalColumnId,
    string FamilyDigest,
    ContextHeaderTargetDto Target,
    MaintainerCapabilityDto Capability,
    string CapabilityFingerprint,
    MaintainerDeclarativeDto DeclarativeSpec,
    int MaxContentUtf8Bytes
);

internal sealed record MaintainerDefinitionBodyV1Dto(
    int SchemaVersion,
    string LogicalColumnId,
    string FamilyDigest,
    ContextHeaderTargetV1Dto Target,
    string CapabilityFingerprint,
    MaintainerDeclarativeDto DeclarativeSpec,
    int MaxContentUtf8Bytes
);

internal sealed record MaintainerDefinitionV1Dto(
    int SchemaVersion,
    string Digest,
    string LogicalColumnId,
    string FamilyDigest,
    ContextHeaderTargetV1Dto Target,
    MaintainerCapabilityDto Capability,
    string CapabilityFingerprint,
    MaintainerDeclarativeDto DeclarativeSpec,
    int MaxContentUtf8Bytes
);
