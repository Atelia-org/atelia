# Galatea.Input

Shared request projection of `galatea.observation.v1` for the Galatea Host and
SessionJournal CLI Recap builds. The public surface is
`GalateaObservationInputProjector.Instance : ISessionInputProjector`.

Text input passes through unchanged. Structured Observation input is strictly
validated and projected with md-json. Other schema IDs fail explicitly. The
library owns the Observation JSON schema, stable bounds and external-string
paths; it has no Web host, configuration, HTTP, capture, storage, hydration or
UI responsibilities. System instructions and delegation tasks remain with their
existing Host consumers.

Projection is invoked only when assembling an LLM request. Reading, auditing,
undo and append proof continue to consume the machine-readable content.
