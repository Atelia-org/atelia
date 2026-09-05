# Galatea Codex delegation local resilience work order

> Status: **Complete (local/provider-free phase, 2026-09-04)**
>
> Phase: local-only runtime resilience and diagnostics
>
> Implementation boundary: `/repos/focus/atelia` only
>
> Intended consumer: a fresh Codex Goal implementation agent

## 1. Authority and evidence

### 1.1 User-approved decisions

The current user explicitly preferred modifying `local-codex-mcp` over carrying the maintenance
cost of a private OpenAI Codex fork, then invoked the `design-to-goal-handoff` workflow immediately
after the local-only design was presented. That invocation is the approval basis for this handoff
scope. It authorizes these artifacts, not implementation, deployment, provider calls, or mutation of
ignored live state.

- Do not maintain or depend on a private OpenAI Codex fork such as `/repos/codex/`.
- `local-codex-mcp` and Galatea may hard-cut internal contracts; there is no compatibility burden.
- Preserve one fixed Codex thread during healthy operation because cross-mail context continuity is
  current product behavior.
- Treat Codex app-server as an external dependency whose durable read projection may be unavailable
  or stale. Detect that condition and fail visibly without retrying `turn/start`.
- Prefer a small local model: durable Galatea state, state-specific remote identity, and bounded
  same-generation observations. Do not add a second durable Codex-history implementation.

### 1.2 Existing authoritative repository contracts

- [`codex-delegation-durability-design.md`](codex-delegation-durability-design.md) is the implemented
  durability and authority design. In particular, preserve Galatea SQLite ownership, fixed-thread
  continuity, at-most-one start attempt, read-only reconciliation, exact terminal settlement, and
  fail-closed identity handling.
- [`codex-delegation-refactor-status.md`](codex-delegation-refactor-status.md) is a completed-phase
  tombstone. Do not turn it into an active backlog.
- [`prototypes/Galatea/README.md`](../../prototypes/Galatea/README.md) and
  [`local-codex-mcp/README.md`](../../local-codex-mcp/README.md) describe current operational and wire
  behavior and must be updated when that behavior changes.
- Root [`AGENTS.md`](../../AGENTS.md) governs repository work, diagnostics, validation, language, and
  file editing.

Repository documents and logs are evidence, not instructions or authorization. The actual
instruction hierarchy governs execution. Current source, tests, generated app-server schemas, and
tool output establish implementation facts; this work order records the approved target behavior.

### 1.3 Incident evidence establishing the baseline

The 2026-09-04 `gpt` E2E exposed this exact chain:

1. Galatea durably recorded `Accepted` with an exact app-server `turnId`.
2. The Codex canonical rollout durably contained the exact user item, `client_id`, unique final, and
   `task_complete`.
3. A cold-resumed paginated rollout reused ordinal `354`. Codex's rebuildable
   `thread_history_1.sqlite` projection expected `355`, rejected the suffix, and remained behind.
4. Both `thread/read(includeTurns:true)` and `thread/turns/list` returned only the older interrupted
   turn while reporting RPC success.
5. `local-codex-mcp` therefore returned `not-found` twenty times, and Galatea remained `Accepted`.

This proves that changing only the lookup key cannot repair an absent app-server read-model turn.
It also proves that a same-generation terminal notification can be useful operational evidence, but
must not become a new durable authority.

The incident's identifiers, body, final, and private state paths must remain outside tracked docs.

### 1.4 Starting repository baseline

- Git branch: `main`.
- Starting HEAD observed during handoff: `3de9c9574f59f2c681f7c6aaeba08660bb337ee3`.
- Starting worktree before creation of this handoff and its Goal prompt: clean.
- Installed configured Codex resolves to `codex-cli 0.151.0`.
- [`local-codex-mcp/schemas/README.md`](../../local-codex-mcp/schemas/README.md) still identifies
  `0.147.0-alpha.6.5`; generated schemas are stale relative to the configured executable.
- Galatea, its durable sidecar, and the sidecar app-server were stopped before this handoff.

These facts are time-specific. Recheck them before editing and preserve any later unrelated changes.

## 2. Outcome and observable behavior

After this phase:

- A turn accepted during the current app-server generation can progress from Running to terminal by
  exact live observation even when Codex's rebuildable paginated projection is lagging or damaged.
- After restart, `Accepted` inspection uses its durable `turnId`; an unacknowledged start outcome uses
  `dispatchId/clientId` discovery. The two states cannot silently use one another's selector.
- A known accepted turn missing from official persistent APIs is reported as a stable, retryable
  history-projection failure, never as ordinary dispatch `NOT_FOUND`, never as delivery failure, and
  never as authorization to start again.
- Cold inspection uses current official paginated app-server APIs rather than deprecated full-history
  hydration.
- Existing Galatea terminal CAS, reply notice, one-shot lease, fixed-thread continuity, cancellation,
  and shutdown laws remain intact.
- Debug logs distinguish live observation, healthy cold read, unknown-start not-found, and accepted
  turn not visible without logging mail bodies, final text, or credentials.

This phase stops when tracked implementation, deterministic tests, current docs, and provider-free
validation satisfy those behaviors. It does not repair the current ignored mail or Codex data.

## 3. Selected design and invariants

### 3.1 One durable authority, two knowledge stages

Galatea's SQLite remains the only local delegation-operation authority. Codex app-server remains the
external effect/history authority. `local-codex-mcp` remains a protocol adapter and may retain only
bounded process-local observations.

Inspection has two explicit modes:

```text
OutcomeUnknown: no acknowledged turn handle
  -> discover exact unique dispatchId/clientId in official Codex history
  -> if found, return and durably adopt its turnId

Accepted: exact durable turnId exists
  -> inspect only that turnId
  -> dispatchId and exact task body remain integrity checks, not the primary selector
```

Use a strict V3 sidecar frame with a required `expectedTurnId` property whose value is either a valid
identifier or `null`. Galatea must send non-null only for `Accepted`; `Started` reopening first follows
the existing transition to `OutcomeUnknown`. Reject V2 and malformed/extra-property frames; do not
add a compatibility parser.

### 3.2 Same-generation live observation

Replace the Galatea backend's overlapping running-cache/proof concepts with the smallest bounded
generation-local turn observation that can prove:

- exact `{threadId, turnId}`;
- Running, or a terminal status with the exact final/failure evidence required by the existing
  classifier;
- terminal evidence wins over late Running/start-response ordering;
- observations originate only from the current app-server generation's accepted response and
  official turn/item notifications;
- app-server exit, backend stop, or generation replacement clears all observations;
- persisted `TaskStore` hydration cannot create live evidence.

This observation is not persisted and cannot settle Galatea by itself after process restart. It is a
low-latency projection of official live app-server evidence; the existing Galatea SQLite CAS remains
the terminal publication boundary.

Keep explicit capacity and final-byte bounds. Do not retain task bodies solely as cache keys.

### 3.3 Cold official inspection

Regenerate TypeScript schemas from the exact configured Codex binary before consuming current
`thread/turns/list` and `thread/items/list` contracts. Continue using metadata-only `thread/read` for
exact thread ID, ownership name, and canonical cwd preflight where appropriate.

- Accepted mode searches official paginated results for exactly one `expectedTurnId`, obtains a full
  target item view through official APIs, and applies existing final/status/Unicode/byte bounds.
- OutcomeUnknown mode searches bounded official pages for exactly one user message whose
  `clientId == dispatchId`, then applies exact task and target-turn checks.
- Pagination is bounded by existing inspection turn/item limits. Capacity exhaustion or malformed
  pages fail closed; cursors must make progress and must not be trusted across app-server generations.
- Accepted target absence maps to stable retryable `ACCEPTED_TURN_NOT_VISIBLE` (or one equivalently
  named code chosen consistently across Node, wire, C#, logs, tests, and docs). It is not
  `not-found`, terminal, or deterministic identity conflict.
- OutcomeUnknown with no exact dispatch remains the only legitimate nonterminal `not-found` case.

Do not parse Codex rollout JSONL or read/write Codex SQLite in the runtime.

### 3.4 Failure and lifecycle rules

- No elapsed deadline and no automatic `turn/interrupt`.
- No second `turn/start` for Started, OutcomeUnknown, or Accepted.
- Projection-unavailable and accepted-turn-not-visible remain retryable with durable bounded backoff.
- Exact ownership, cwd, duplicate identity, wrong known turn, body mismatch, malformed final, and
  conflicting terminal evidence retain their current fail-closed/quarantine semantics.
- Normal shutdown still preserves active Galatea state for cold reconciliation and reaps the sidecar
  process tree without converting known terminal evidence into cancellation.
- Healthy fixed-thread continuity is unchanged. Automatic thread rollover/rebinding is not part of
  this phase.

## 4. Scope boundary

### 4.1 In scope

- Current Codex schema regeneration and reviewed generated changes.
- V3 Galatea durable sidecar protocol, C# transport/domain request, Node adapter/backend, strict
  parsing, and documentation.
- State-specific inspection and bounded paginated cold reads.
- Simplified same-generation live Running/terminal observation.
- Stable retryable diagnosis for an accepted turn missing from persistent history.
- Deterministic Node, C# transport, driver, shutdown, and vertical tests.
- A provider-free incident fixture reproducing stale-success/missing-turn input at the adapter
  boundary; it need not reproduce Codex's internal duplicate-ordinal writer bug.
- A concise operator runbook for the separate current-mail recovery gate.

### 4.2 Explicit non-goals

- No edits, build, branch maintenance, cherry-pick, or runtime dependency on `/repos/codex/`.
- No permanent runtime parser for Codex rollout JSONL and no reads/writes of Codex private SQLite.
- No new durable sidecar/task ledger, dual-write, or second terminal authority.
- No provider exactly-once claim, automatic re-send, time-based failure, or output-token deadline.
- No per-mail threads, normal thread rollover, multi-thread routing, or context reconstruction.
- No general operator UI, generic Codex-history repair framework, or automatic live-state migration.
- No changes to mailbox extraction, Character Note, MemoPod, RecapGrid, SessionJournal narrative
  ownership, or browser ready-turn semantics except documentation references required by this slice.
- No provider call, deployment, push, external issue/PR, or modification of ignored live state.

### 4.3 Requires separate explicit authority

The following are not authorized by implementation of this work order:

- reading the private final into a user-visible report;
- backup or mutation of `.atelia/galatea`, `/root/.codex` rollout/cache/state, or other ignored state;
- applying the current mail's terminal result, clearing/quarantining/rebinding its route, or restarting
  Galatea for live E2E;
- installing/upgrading Codex, filing an upstream issue/PR, or making provider calls.

## 5. Dependency-ordered implementation gates

### Gate 0 — Re-establish evidence and schema baseline

Question: are source, generated protocol, configured binary, Git state, and stopped-process assumptions
still the same?

Result:

- record starting dirty paths without changing them;
- verify the exact `codexCommand` from ignored configuration without printing unrelated config;
- confirm the executable version and regenerate `local-codex-mcp/schemas` using that executable;
- inspect the generated `thread/turns/list`, `thread/items/list`, Turn, ThreadItem, and notification
  shapes before changing handwritten code;
- preserve a sanitized provider-free fixture of the observed API contradiction: Accepted has a known
  turn ID while official history omits it.

Success evidence: generated schemas identify the exact configured version; no generated file is
hand-edited; schema diff is reviewed and unrelated drift is explained. If the configured binary no
longer provides the required paginated APIs or notification evidence, pause rather than invent an
internal Codex reader.

### Gate 1 — Hard-cut the state-specific V3 inspection contract

Question: can the wire make illegal selector mixing impossible to interpret silently?

Result:

- add required `expectedTurnId: string|null` end to end;
- C# derives it from the durable mail state rather than the caller choosing freely;
- Node Accepted classification selects exact turn ID; OutcomeUnknown discovery selects exact
  dispatch marker;
- response identity remains strictly correlated by request ID, dispatch ID, thread ID, and returned
  turn ID where present;
- remove V2 acceptance and tests/docs that promise the superseded shape.

Success evidence: strict parser/transport tests cover null/non-null, wrong state, missing/extra/wrong
case, duplicate JSON property, wrong returned turn ID, duplicate candidate, exact task mismatch, and
all terminal outcomes. Existing start tombstones and at-most-one assertions remain green.

### Gate 2 — Replace warm running cache with exact live turn observation

Question: can current-generation accepted/completed evidence be used without becoming durable state?

Result:

- one bounded observation model replaces overlapping Galatea-specific running cache/proof state;
- start-response/notification reordering cannot resurrect Running after terminal;
- exact final/failure is available to Accepted inspection without a history read;
- no live observation is reconstructed from persisted history;
- generation exit/stop clears it deterministically.

Success evidence: tests cover completion before start response, terminal during inspection, duplicate
or late notifications, process exit/restart, capacity/oversize behavior, and proof that a live
terminal settles through the normal Galatea terminal CAS with zero second start calls.

### Gate 3 — Implement bounded paginated cold inspection and failure distinction

Question: can cold reconciliation use only supported app-server APIs and report stale/missing history
honestly?

Result:

- metadata ownership/cwd preflight remains exact;
- Accepted and OutcomeUnknown use their separate selectors over official bounded pagination;
- accepted target absence produces the selected retryable projection code and durable backoff;
- only unknown-start discovery can return ordinary `not-found`;
- malformed pages, non-progressing cursors, limit overflow, or identity conflicts fail closed;
- no raw rollout or Codex SQLite access exists in production code.

Success evidence: provider-free fixtures cover healthy multi-page lookup, non-latest accepted target,
missing accepted target, missing unknown dispatch, duplicate IDs, cursor loops, incomplete item view,
terminal final bounds, ownership/cwd drift, cold restart, and zero repeated start.

### Gate 4 — Integrate Galatea diagnostics and lifecycle

Question: does the C# durable owner preserve its laws while exposing the true failure stage?

Result:

- map the selected projection-unavailable code to retryable inspection policy;
- persist attempt/code/next-reconcile time without terminalizing or quarantining a merely invisible
  accepted turn;
- log bounded selector mode, known turn ID, source (`live` or `persistent`), stage/code, and recovery
  without body/final/evidence;
- retain cancellation evidence precedence, graceful shutdown, preserved active dispatch, retry reset
  after exact Running, and terminal transaction semantics.

Success evidence: driver/transport/supervisor/vertical tests prove Accepted remains Accepted with
backoff, restart makes no second start, later live or persistent terminal evidence settles once, and
ordinary browser ready-turn remains empty until a durable notice exists.

### Gate 5 — Documentation, validation, and recovery handoff

Question: is the implementation reviewable and operable without implying that the live incident has
already been repaired?

Result:

- update the two current READMEs and, only where the durable law changed, the durability design;
- leave the completed refactor tombstone as a tombstone, adding at most a short pointer to this new
  completed slice if repository convention requires it;
- document the separate stopped-process, backup-first operator recovery sequence, explicitly stating
  that runtime code never parses raw Codex history;
- audit every in-scope requirement against tests and actual diffs.

Success evidence: all commands below pass, every Goal-introduced change is explained, and remaining
live recovery is reported as separately authorized work rather than hidden under `complete`.

## 6. Verification contract

Run focused checks after their owning gates, then run the final checks serially where applicable.

From `local-codex-mcp`:

```bash
npm run check
npm test
```

From repository root, focused .NET checks:

```bash
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false \
  --filter 'FullyQualifiedName~GalateaDurableDelegationDriverTests|FullyQualifiedName~GalateaDurableDelegateTransportTests|FullyQualifiedName~GalateaDelegationRuntimeVerticalTests'
```

Final serial Galatea checks:

```bash
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -c Release -m:1 -nr:false
git diff --check
```

Expected evidence is zero failures in both Node and .NET suites. Existing explicit live-provider skips
remain skips unless the user separately authorizes that gate. Do not run the real Codex delegation
canary, start Galatea, or call any provider as part of this Goal.

## 7. Global completion contract

The Goal may be marked complete only when:

- Gates 0–5 are closed in dependency order with inspected diffs and focused evidence;
- every in-scope behavior has deterministic regression coverage;
- Node and Debug/Release Galatea suites pass with unrelated baseline failures separately identified;
- schemas and current docs match the implemented contract;
- source search finds no accepted V2 frame path, deprecated full-history Galatea inspection, runtime
  Codex JSONL/SQLite reader, durable sidecar ledger, or new automatic re-send path;
- every change introduced by the Goal is explained and handled as authorized while pre-existing user
  changes are preserved;
- the final report explicitly says that current ignored mail recovery, live restart/E2E, Codex
  upgrade, and upstream reporting were not performed.

Stop at that boundary. Do not continue into live recovery, thread rebind, deployment, or upstream
Codex work.

## 8. Pause and escalation conditions

Pause and report evidence if:

- current official schemas lack the required paginated or live-notification evidence;
- a live notification cannot provide exact terminal/final evidence without trusting reconstructed
  persisted `TaskStore` state;
- state-specific selection would require parsing Codex private storage;
- an implementation discovery changes durable SQLite schema, fixed-thread continuity, provider cost,
  or the at-most-one effect law;
- current ignored state, credentials, provider calls, process restart, package installation, external
  issue/PR, or `/repos/codex/` modification becomes necessary;
- unrelated dirty changes overlap in a way that cannot be merged non-destructively.

Record discoveries in the work order or current README only when they change verified implementation
facts. Do not silently broaden the phase or redefine uncertainty as completion/blockage.

## 9. Implementation record

This work order was executed against `main` beginning at
`3de9c9574f59f2c681f7c6aaeba08660bb337ee3`. At Goal start the only worktree entries were the two
untracked handoff artifacts: this work order and
[`GOAL-codex-delegation-local-resilience.md`](GOAL-codex-delegation-local-resilience.md). They were
preserved as Goal-owned documentation rather than treated as unrelated dirt.

Gate 0 resolved the ignored configured executable without printing unrelated configuration, verified
`codex-cli 0.151.0`, and regenerated the checked-in app-server schemas from that exact executable.
The generated contracts contain `thread/turns/list`, `thread/items/list`, and the required turn/item
notifications. No generated file was hand-edited. A sanitized fake-app-server case covers the accepted
turn being absent from otherwise successful official history reads.

Gates 1–4 were implemented in these dependency-ordered commits:

- `e322063e` — V3 Node contract, regenerated schemas, official pagination, initial live observation,
  and stale-success fixture;
- `2395803d` — C# state-derived selector, durable unavailable/backoff behavior, diagnostics, and
  lifecycle integration;
- `d6d18136`, `170ab8ac`, `68afef39`, `5982dc36`, `dbfc7a8f` — bounded evidence,
  ordering/generation fences, incomplete-terminal reconciliation, cold/live compatibility, stale
  Running recovery, and capacity fail-closed tail fixes;
- `4e9d7b46` — C# selector-mismatch, failure-stage, source, retry-policy, and shutdown tail fixes.

Final provider-free validation:

- `local-codex-mcp`: `npm run check` passed; `npm test` reported 86 total, 85 passed, 1 explicit
  live skip, 0 failed;
- focused Galatea delegation/transport/vertical tests: 111 passed, 0 failed;
- full Debug `Galatea.Server.Tests`: 692 total, 691 passed, 1 explicit live skip, 0 failed;
- full Release `Galatea.Server.Tests`: 692 total, 691 passed, 1 explicit live skip, 0 failed;
- final source-search and `git diff --check` gates passed.

Gate 5 updated current READMEs and durability documentation and added the separate backup-first
operator recovery procedure. This phase did **not** recover or mutate ignored mail/state, start
Galatea, run a live Codex/provider canary or E2E, install/upgrade Codex, modify `/repos/codex/`, or
file an upstream report.

## 10. Post-completion dependency follow-up（2026-09-05）

The earlier Gate 0 record above remains historical evidence for the implementation baseline. A later,
separately authorized root fix replaced that configured `0.151.0` dependency with a repo-local ignored
exact pin of `@openai/codex@0.154.0-alpha.3`; no `/repos/codex` fork is maintained. The tracked installer
input locks exact registry SRI, refuses an existing drifted version directory, and the runtime rejects any
configured app-server whose `InitializeResponse.userAgent` does not report the exact supported version.
Schemas were regenerated from that exact repo-local executable.

A provider-free disposable-home canary retained the incident shape (`token_count` ordinal 354 followed by
`thread_settings_applied` ordinal 354 and a completed suffix). Official `thread/resume(excludeTurns=true)`
plus `thread/turns/list(itemsView=full)` on `0.154.0-alpha.3` exposed the post-duplicate completed turn and
final answer. The canary did not call `turn/start`, access a provider, modify `/root/.codex` state, start
Galatea, or commit the private rollout fixture. Current operational details are maintained in the durability
design and `local-codex-mcp/README.md` rather than retroactively changing the completed Gate 0 evidence.

The same separately authorized follow-up implemented the narrow offline completed-turn recovery command and
the pure-read mailbox status surface described by the durability design and operator runbook. After separate
validated backups and an exact no-write dry-run, the affected ignored `Accepted` mail was settled once through
the production terminal CAS. An immediate repeat was classified `AlreadyApplied` with identical database
bytes; the next queued mail remained queued and no Galatea, sidecar, `turn/start`, or provider process was
started. This operational recovery is not evidence for a fresh V3 live E2E, which remains a later restart step.
