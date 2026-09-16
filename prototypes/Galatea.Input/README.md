# Galatea.Input

Shared request projection of `galatea.observation.v1` and `galatea.observation.v2` for the Galatea Host and
SessionJournal CLI Recap builds. The public surface is
`GalateaObservationInputProjector.Instance : ISessionInputProjector`.

Text input passes through unchanged. Structured Observation input is strictly
validated and projected with md-json. V1 heartbeat remains exact at
`externalIntervalMinutes: 10`; new V2 heartbeat records carry an immutable
`1..525_600` interval snapshot. V2 accepts only heartbeat activation, while
player-action, delegate-reply and inbound-mail remain V1. Other schema IDs fail explicitly. The
library owns the Observation JSON schema, stable bounds and external-string
paths; it has no Web host, configuration, HTTP, capture, storage, hydration or
UI responsibilities. System instructions and delegation tasks remain with their
existing Host consumers.

Projection is invoked only when assembling an LLM request. Reading, auditing,
undo and append proof continue to consume the machine-readable content.
