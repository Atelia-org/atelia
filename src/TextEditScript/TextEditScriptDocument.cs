namespace Atelia.TextEditScript;

public sealed record TextEditScriptDocument {
    private static readonly IReadOnlyList<TextEditOperation> s_emptyOperations = Array.AsReadOnly(Array.Empty<TextEditOperation>());

    public TextEditScriptDocument(IReadOnlyList<TextEditOperation> operations) {
        ArgumentNullException.ThrowIfNull(operations);
        Operations = Freeze(operations);
    }

    public IReadOnlyList<TextEditOperation> Operations { get; }

    public static AteliaResult<TextEditScriptDocument> ParseXml(string xml)
        => TextEditScriptXml.Parse(xml);

    public AteliaResult<TextBlockSnapshotDocument> ApplyTo(
        TextBlockSnapshotDocument snapshot,
        TextEditScriptApplyOptions? options = null)
        => TextEditScriptApplier.Apply(snapshot, this, options);

    public string ToXml()
        => TextEditScriptXml.Format(this);

    private static IReadOnlyList<TextEditOperation> Freeze(IReadOnlyList<TextEditOperation> operations)
        => operations.Count == 0 ? s_emptyOperations : Array.AsReadOnly(operations.ToArray());
}
