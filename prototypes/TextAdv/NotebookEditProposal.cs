using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal sealed record NotebookEditProposal(
    string CanonicalScriptXml,
    TextEditScriptDocument Script,
    TextBlockSnapshotDocument PredictedAfterSnapshot,
    string ActionSummary,
    string ValidatorPayload
);
