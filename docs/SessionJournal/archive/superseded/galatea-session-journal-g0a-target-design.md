# Galatea SessionJournal G0A Target Design

> Status: Implemented (2026-08-01) for `galatea-session-journal-cutover-plan.md` G0A.
> Scope: recovery runtime identity, desired setup reconciliation, and explicit provisioning boundary.

## 1. Outcome

G0A gives a Host enough public API to make two decisions before constructing an online runtime:

1. whether the exact durable tail needs no pending runtime, a newly selected completion runtime, an
   exact frozen completion runtime, or an exact tool continuation runtime;
2. whether desired model/surface/system-prompt intent may be synchronized at the exact current raw
   head.

The raw SessionJournal remains the authority for recovery and governing setup. `Completion` owns
connection/adapter fingerprinting and exact registry binding. CLI and Galatea remain thin composition
roots which map between those neutral public surfaces.

G0A does not switch Galatea's production session path, implement recent-turn projection/Undo, or run
a network LLM acceptance.

## 2. Recovery requirement projection

`SessionJournalEngine.InspectRuntimeRecoveryRequirements()` is read-only and returns a sealed union
bound to one captured raw head:

- `NoRuntimeRequired`: only `Empty` or `Idle`; there is no pending dispatch to recover. A later
  user-initiated Send is a separate Host operation.
- `FailedTurnMustBeAbandoned`: exact `CompletionAttemptFailed` / `TurnFailed`; exposes only
  `FailedHead`, which the Host must pass to `AbandonFailedTurn` before setup reconciliation or Send.
- `NewRequestRequired`: `AwaitingAgentAction`; no completion target has been frozen yet. The Host
  supplies a current completion runtime, but must not append setup inside this active turn.
- `FrozenCompletionRequired`: `CompletionRequestPrepared` or `CompletionAttemptStarted`; exposes only
  the non-secret completion target, client/API identity, visible-tool-set fingerprint, optional tool
  runtime identity, and whether provider dispatch was not started or has an uncertain prior outcome.
- `ToolContinuationRequired`: `AwaitingToolExecution`; exposes the exact durable tool runtime and
  whether tool dispatch has started. It does not pretend that the earlier completion target is frozen
  for the completion request which may follow the tool result.

The tool-continuation variant is an intentional correction to the earlier sketch. A pending tool
operation has frozen tool identity but no pending frozen completion dispatch; representing it as
either `NewRequestRequired` or `FrozenCompletionRequired` would create an invalid shape.

The projection reuses a sanitized snapshot retained by tail recovery after validating the Prepared
manifest. It must not expose or reconstruct request content, tool definitions, raw tool arguments,
operation ids, observations, prompts, or credentials. Corrupt raw lineage/manifest remains an
exception, not a typed availability result.

## 3. Completion identity and exact binding

`Atelia.Completion` publicly owns a non-secret `CompletionDispatchIdentity` and the stable
connection/request-adapter fingerprint algorithms. It does not reference SessionJournal types.

`CompletionConnectionRegistry.BindExact(...)`:

1. looks up the durable `ConnectionId` without default fallback;
2. compares kind and connection metadata fingerprint before creating a client;
3. creates only that candidate client;
4. compares client name, API spec, and request-adapter fingerprint;
5. returns typed `Bound` or `Unavailable`.

`Unavailable` classifies missing/drifted identity. If the selected factory itself cannot construct the
candidate client, its existing sanitized configuration/operational exception propagates; identity
binding does not catch arbitrary factory exceptions or copy their possibly endpoint-bearing messages
into a persisted/typed identity result.

The Host explicitly maps a bound/current Completion identity to
`SessionCompletionTargetIdentity`. Missing or changed durable connections are recovery-unavailable;
they never silently select the current default.

## 4. Desired setup reconciliation

`SessionJournalEngine.ReconcileDesiredSetup(expectedHead, desired)` is one exact-head core operation.
It accepts only Host-controlled `ModelId`, `CompletionSurfaceId`, and `SystemPrompt`. It derives the
replacement runtime config from the governing raw setup, thereby preserving repository-owned
`Schema` and `DerivedContext` values.

The operation is legal only at exact `Idle`:

- `Empty` returns `Unprovisioned`;
- `TurnFailed` returns `FailedTurnMustBeAbandoned`;
- every active turn/recovery phase returns `ActiveTurn`;
- a stale expected head returns `Retryable`.

Restricting reconciliation to `Idle` corrects a conflict in the earlier cutover plan. G0B requires an
exact `TurnFailed` head before abandoning a failed turn; appending a setup-only suffix first would
destroy that exact shape. The Host must abandon first, then reconcile at the resulting Idle head.
Because this is a pre-Beta direct cut, an old `TurnFailed + setup/Observation suffix` is unsupported and fails
closed; inspection does not scan backward to synthesize a compatible failed-head authority.

Comparison is ordinal and prompt content is not normalized. Runtime setup is appended first and
system prompt second. If the second append fails, the runtime intent remains durable; a later exact
retry observes it as already satisfied and appends only the missing prompt. There is no rollback,
compensation event, or setup transaction state machine.

## 5. Host ordering

For a new Send at Idle:

1. open an already provisioned raw repository and inspect recovery requirements;
2. resolve selected connection metadata without creating its client;
3. reconcile desired runtime/system setup at the captured Idle head;
4. prepare Recap from the returned new raw head;
5. create/bind the concrete agent runtime only after setup and Recap readiness succeed;
6. enter the captured-head-bound `SendAsync` overload so a later writer cannot retarget the composed
   runtime; append Observation and dispatch.

For `AwaitingAgentAction`, setup reconciliation is forbidden. The selected connection's model and
surface must match the governing setup before the Host resumes the new request. For Prepared/Started,
the Host ignores current/default selection and exact-binds the durable completion identity. Started
with the default `Refuse` policy returns before client creation, provider call, or raw mutation.
Prepared/Started enter the captured-head-bound `ResumeAsync` overload; an intervening tail advance
restarts inspection instead of executing a later operation which happens to share the same identity.

`NewRequestRequired` still performs Recap preparation because it is about to create a new Prepared
request, but it never reconciles setup inside the active turn. `FrozenCompletionRequired` is
active-config zero-touch. `ToolContinuationRequired` remains explicitly unsupported in the first
empty-tool Galatea slice; a later tool-capable Host exact-binds the tool runtime and only consults
current planning config when it reaches the next new-request boundary.

## 6. Provisioning boundary

Galatea Send does not call `SessionJournalEngine.Create`, config init, or Recap Store create. Account
provisioning is a separate operator workflow using the production importer/create commands, planner
config initialization, and Store creation.

An absent repository or a repository shell without a valid SessionJournal head is explicitly
unavailable. A repository with missing/invalid planner config or Store is likewise unavailable before
Observation or agent dispatch. Inspection/open paths must not turn either case into a new partial
repository. Raw corruption and I/O errors remain distinct failures and are not disguised as
unprovisioned.

## 7. Acceptance gates

- all recovery phases project the correct sealed variant and exact captured identity;
- Prepared A still binds A after current/default selection changes to B;
- missing or drifted A is typed unavailable without fallback;
- Started `Refuse` creates no provider client, makes no call, and writes no raw event;
- Idle connection switch appends runtime setup before Recap/Observation and preserves
  `Schema/DerivedContext`;
- identical desired setup appends nothing;
- prompt-only, runtime-only, both-changed, stale-head, partial-second-append retry, Empty,
  `TurnFailed`, and active-tail cases are covered;
- recovery and active-turn paths append no setup;
- empty/half-provisioned repository checks leave no new raw/config/Store material;
- CLI acts as a public-API reference Host; Galatea adoption remains a later G1 slice.
