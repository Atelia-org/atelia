# Galatea.Input

Shared request projection of `galatea.observation.v1` through `galatea.observation.v5` for the Galatea Host and
SessionJournal CLI Recap builds. The public surface is
`GalateaObservationInputProjector.Instance : ISessionInputProjector`.

Text input passes through unchanged. Structured Observation input is strictly
validated and projected with the `Atelia.MdJson` package pinned in
`eng/MdJsonDependency.props`. V1 heartbeat remains exact at
`externalIntervalMinutes: 10`; V2 heartbeat records carry an immutable
`1..525_600` interval snapshot. V2 accepts only heartbeat activation; V3/V4
add connection state snapshots. New Host inputs use V5 and require the complete
V4 connection snapshot, including frozen connection names. V1–V4 remain readable
under their original contracts; projection does not rewrite historical records
or frozen prepared inputs. Other schema IDs fail explicitly. The
library owns the Observation JSON schema, stable bounds and candidate string
paths; md-json only moves candidate strings that need JSON escaping into fenced
blocks. It has no Web host, configuration, HTTP, capture, storage, hydration or
UI responsibilities. System instructions and delegation tasks remain with their
existing Host consumers.

V5 adds `email-inbound` alongside the existing input kinds. Its sender is always
the fixed Galatea runtime identity; its declared `action.from` is a single ASCII
mailbox address, never an authenticated Player or Character identity. The action
freezes message ID, recipient, subject, full body and attachment count. External
mail carries no Player-turn notices or recalls. Request projection adds a fixed
external-data notice and states that attachment contents are unavailable; this
notice is not a second persisted copy of the incoming message. The same module
owns the narrow canonical external-address syntax used by the Host parser.

Projection is invoked only when assembling an LLM request. Reading, auditing,
undo and append proof continue to consume the machine-readable content.
