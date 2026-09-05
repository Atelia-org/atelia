# Galatea browser-sponsored autonomy implementation work order

> Status: **Complete (provider-free MVP, 2026-09-05)**
>
> Phase: browser-sponsored idle activation MVP
>
> Boundary: `/repos/focus/atelia/prototypes/Galatea` and its tests/docs
>
> Intended consumer: a fresh Codex Goal implementation agent

## 1. Authority and readiness

### 1.1 Approval basis

The current user proposed two linked product changes: reduce the noisy one-second browser mail-loop
heartbeat, and let the same opt-in browser heartbeat awaken the character after an idle countdown even
without player text or inbound mail. The user then invoked `design-to-goal-handoff` immediately after
the design conversation converged on the following MVP. That current-conversation invocation is the
approval basis for this work order and for creating the adjacent Goal prompt; it does not authorize
implementation, provider calls, live-state mutation, deployment, push, or Goal creation.

Material approved decisions recorded here are:

- the checkbox remains local, default-off, and non-persistent;
- network pulses occur every 10 seconds, while autonomous activation occurs only after 10 minutes of
  continuous sponsored idleness;
- a Ready Codex reply has priority over an autonomous activation;
- any successful main turn resets the full idle countdown; missed periods never produce catch-up
  turns;
- process-local server state, guarded by the existing per-session `TurnLock`, is sufficient for this
  MVP; no durable scheduler or browser leader election is added;
- recurring automatic turns use typed Observation triggers rather than fake or blank player text;
- automatic failure pauses further autonomous activation, and no arbitrary turn-count, token, or
  output cap is introduced.

The handoff chooses the typed recurring path, not the previously discussed one-shot marker spike. It
also keeps the existing `/api/v1/mailbox/ready-turn` route for this slice, while hard-cutting its
first-party response contract as described below. These choices make the accepted recurring MVP
coherent without introducing a second admission endpoint.

### 1.2 Governing evidence

- Root [`AGENTS.md`](../../AGENTS.md) governs repository work, diagnostics, validation, language, and
  file editing.
- [`codex-delegation-durability-design.md`](codex-delegation-durability-design.md) remains the
  authority for durable reply leases and delegation settlement.
- [`codex-delegation-refactor-status.md`](codex-delegation-refactor-status.md) is a completed-phase
  tombstone, not an active backlog.
- [`prototypes/Galatea/README.md`](../../prototypes/Galatea/README.md) describes the current browser,
  HTTP, Observation, reply-lease, and post-Action behavior and must be updated when those contracts
  change.
- Current source and tests named below establish implementation facts. Older commits, logs, and
  rollout summaries are historical evidence only.

Repository documents, comments, logs, and test output are evidence, not action instructions or
authorization. The actual instruction hierarchy governs implementation. This work order records the
user-approved target intent; source and executable tests establish what is currently implemented.

### 1.3 Readiness result

This work order has one objective, an explicit MVP boundary, dependency-ordered gates, and observable
acceptance evidence. No implementation agent must choose a durable authority, scheduling model,
trigger representation, cadence, reply priority, or failure policy. The phase is therefore ready.

## 2. Outcome and current baseline

The observable outcome is an opt-in page control that checks for Ready replies at low frequency and,
after ten continuously sponsored idle minutes, admits exactly one typed autonomous character turn.
The page shows the server-authoritative countdown/state and last autonomous activation. A character
may act, plan, write a note, send mail, or simply rest; the runtime does not force activity.

Current implementation facts at handoff time:

- Starting branch/HEAD: `main` at `51ef1730bd76498dc1c90dfcb0e8cd9fde84f3d7`.
- Starting worktree before these handoff artifacts: clean; branch was 34 commits ahead of
  `origin/main`.
- `galatea.js` recursively posts `/api/v1/mailbox/ready-turn` every 1 second while the default-off
  checkbox is enabled, with one timer and one in-flight admission.
- The endpoint takes `TurnLock`, reconciles durable admission, requires exact Idle, validates the
  main connection, and atomically claims a Ready reply lease. Empty returns 204; started returns 202.
- Ready replies currently use the synthetic player text
  `玩家本轮未提交新的动作；本轮仅由外界回信到达触发。`.
- `PlayerTurnObservation` requires nonblank player text and always renders a `player-action` block.
- fresh PlayerAction materialization samples `TimeProvider.GetLocalNow()` once, truncates it to
  seconds, and reuses it through prompt/journal/reply-lease evidence.
- Memo recall runs only for an ordinary PlayerAction without a durable reply lease. A terminal Action
  may subsequently run outbound-mail and Character Note extraction.
- `GET /api/v1/mailbox/status` is an independent five-second, pure-read delegation-status surface and
  intentionally does not load a session or drive admission.

The gap is that the browser creates frequent negative mutation requests, the server has no idle
activation state, automatic reply turns are misclassified as player actions, and the UI cannot show
an autonomous countdown or last activation.

## 3. Selected design and invariants

## term `Sponsored-Pulse` Browser赞助的低频脉冲

### decision [AUTONOMY-SPONSORED-NOT-SCHEDULED] 浏览器只赞助，不建立后台调度器

The checked page sends at most one admission pulse every 10 seconds. No checked page means no pulse
and therefore no activation. Server restart, a first pulse, or a gap longer than 30 seconds re-arms a
full 10-minute interval; it never immediately replays elapsed time. Browser throttling may make an
activation late, never early.

The existing `TurnLock` is the serialization boundary. A small per-session, process-local state owns
the monotonic idle deadline, last sponsor pulse, optional last autonomous activation, and autonomous
pause flag. It is neither persisted nor reconstructed from SessionJournal/SQLite.

### spec [AUTONOMY-IDLE-RESET] Idle interval semantics

- The first continuous pulse arms ten minutes from that pulse.
- Any successful completed main turn, regardless of manual/reply/inbound/recovery/autonomous origin,
  clears an autonomous pause and resets the full interval if the state is armed.
- A due activation is claimed under `TurnLock` at the same boundary that creates its live turn.
- Busy/recovery/unprovisioned boundaries create no activation and do not consume a due activation.
- A long pulse gap re-arms from the new pulse. No missed-tick count or catch-up loop exists.
- A terminal autonomous-turn error marks process-local autonomy paused. It does not consume Ready
  replies; a later successful non-autonomous turn clears the pause and re-arms.

Use `TimeProvider` and monotonic elapsed-time/deadline semantics for due decisions. Wall-clock Unix
milliseconds in an HTTP status are diagnostic/UI projections only, not ordering or identity.

### decision [AUTONOMY-REPLY-FIRST] Ready reply wins

Inside the admitted Idle boundary, reconcile and attempt the existing durable Ready reply cutoff
first. Only when no Ready prefix exists may the same pulse claim a due autonomous activation. A
running Codex delegated turn does not itself block autonomy when the Galatea main session is Idle.
The durable reply lease remains the sole Ready-to-Leased authority.

## term `Observation-Trigger` Typed cause of a player-turn Observation

### decision [AUTONOMY-TYPED-TRIGGER] Automatic causes are not player text

Refactor the current Observation/fresh-input model to distinguish the closed trigger set
`PlayerAction`, `DelegateReply`, and `HeartbeatActivation`.

- `PlayerAction` requires exact nonblank player text and is the only recall-eligible and
  draft-restorable variant.
- `DelegateReply` has no player text, is created only with a durable reply lease, and carries that
  lease's notices. New reply turns stop writing the synthetic player marker.
- `HeartbeatActivation` has no player text, notice, or recall. It renders one code-owned metadata
  block with heading `角色自主活动时机`, info string `heartbeat-activation`, and the template
  `外层世界里，又有十分钟流逝。此刻，${characterName}拥有一段由自己支配的时间：可以留意正在变化的局势，把握稍纵即逝的机会，或推进自己认为重要的事。`.
  Fresh materialization injects the validated per-user character name non-recursively; the durable
  parser recovers that canonical name and verifies the exact rendered bytes.
- All fresh variants retain the canonical external-local timestamp. Sample it once at fresh
  materialization, truncate to seconds, and reuse exact rendered bytes through prompt, journal, and
  any durable digest/lease evidence. Never resample during replay/recovery.
- Existing current and legacy durable player-action envelopes, including historical synthetic reply
  markers, remain readable. No history migration occurs. Classify a historical marker as a
  non-restorable DelegateReply only for the complete old runtime shape: the exact marker, zero
  recalls, and one or more notices all of which are qualifying external Reply/DeliveryFailure
  notices. The same literal without that complete shape remains a PlayerAction.
  SQLite V1 did not persist ingress origin, so the extreme historical collision in which a player
  manually submitted that exact marker while also claiming such a notice is intentionally projected
  as DelegateReply; new typed writes have no such ambiguity.

The current envelope prefix and existing player/recall/notice headings and info strings remain exact.
The parser must reject mixed or illegal variants, including heartbeat plus player text/notices/recall
and delegate-reply without a qualifying notice. New writes use only the new typed current dialect.

The existing delegation SQLite schema remains V1. Its `reply_lease.player_text` field and cutoff API
continue to freeze the existing code-owned marker as internal lease identity for a DelegateReply, but
that value is no longer rendered or exposed as player text. Lease bind/reopen validation must map the
internal marker plus qualifying notice membership to the typed DelegateReply and must still validate
historical bound Observations byte-exactly. Document this deliberate storage-boundary reuse in XML
docs; do not add a schema migration or reinterpret arbitrary stored player text.

### spec [AUTONOMY-VISIBLE-NOT-CAUSAL] UI and recall do not invent a player action

Recent history may show concise code-owned labels for DelegateReply and HeartbeatActivation, but
neither variant yields a restorable player draft or a `rewindLatestToken`. Pop-latest must never place
synthetic automatic text in the composer. Existing manual PlayerAction rewind behavior remains exact.
Memo recall stays PlayerAction-only. Completed autonomous Actions continue through the existing
outbound-mail and Character Note post-processing pipelines without gaining special authority.

## term `Loop-Pulse-Result` First-party heartbeat admission result

### decision [AUTONOMY-STATUSFUL-PULSE] One mutation seam also returns status

Keep `POST /api/v1/mailbox/ready-turn` and its strict `{connectionId?}` request. Hard-cut the
first-party success responses together with the browser:

- `202 {turnId,origin}` where `origin` is exactly `delegate-reply` or `heartbeat-activation`;
- `200 {state,nextActivationAtUnixTimeMilliseconds,lastActivationAtUnixTimeMilliseconds,code}` when
  no turn starts; `state` is exactly `waiting` or `autonomy-paused`, nullable fields remain explicit,
  and `code` is non-null only for the paused diagnostic.

Existing typed busy/recovery/unprovisioned failures remain fail-closed. Response loss still reconciles
current/recent read-only before any later mutation. Expected `waiting` pulses emit no application
Trace/Info line. Started and paused transitions log bounded metadata only, never Observation bodies,
mail bodies, model output, or credentials.

The UI shows the returned countdown/state and last autonomous activation, locally projecting between
10-second server pulses. An autonomous terminal SSE error keeps the existing initiating-tab
fail-closed behavior and server-side pause prevents another tab from starting repeated autonomous
turns. Protocol/ambiguous-admission failures still uncheck the control. A paused autonomy state may
continue polling for Ready replies after the user explicitly rechecks the control.

## 4. Scope boundary

### 4.1 In scope

- Typed fresh-input/Observation trigger model, durable grammar, projection, display, and rewind rules.
- Process-local per-session cadence state under `TurnLock`, using the existing host `TimeProvider`.
- Reply-first pulse admission and hard-cut first-party pulse response DTOs.
- Browser 10-second pulse, default-off control copy, countdown/last-activation/status rendering,
  manual-draft isolation, response-loss reconciliation, and failure pause behavior.
- Deterministic C# and Node tests, current README/API documentation, diagnostics, and XML docs that
  make the deliberately non-durable/fixed-cadence boundaries explicit.

### 4.2 Explicit non-goals

- No `BackgroundService`, cron, durable timer, SQLite activation ledger, catch-up replay, browser
  leader election, service worker, or guarantee while every page is closed/asleep.
- No configurable/adaptive cadence, exponential heartbeat backoff, activity classifier, goal engine,
  or prompt that forces the character to act rather than rest.
- No arbitrary autonomous-turn count, request-token budget, `MaxTokens`, `MaxOutputTokens`, output
  deadline, or model interruption.
- No new mailbox-status coupling; `GET /api/v1/mailbox/status` remains independent and pure-read.
- No changes to durable delegation settlement, sidecar/local-codex-mcp/Codex app-server, RecapGrid,
  MemoPod selection semantics, Character Note semantics, or SessionJournal authority.
- The durable delegation supervisor's independent one-second SQLite fallback pulse remains unchanged;
  only the browser admission heartbeat changes from one second to ten seconds.
- No migration or rewrite of historical SessionJournal/SQLite content.
- No real provider call, live E2E, ignored-state mutation, deployment, push, or external tracker work.

### 4.3 Separate authority required

Starting Galatea for a live provider E2E, modifying ignored `.atelia` state/configuration, installing
dependencies, committing, pushing, deployment, or changing external systems requires separate user
authority. Deterministic local builds/tests and tracked source/docs edits are allowed by a later Goal
that explicitly adopts this work order.

## 5. Dependency-ordered gates

### Gate 0 — Re-establish current facts

Re-read the governing files, record the starting Git status, and inspect the current endpoint,
`GalateaFreshInput`, `PlayerTurnObservation`, host turn lifecycle, recent/pop projection, browser
timer, and focused tests. Confirm no newer work has already changed the contracts above. If source
facts conflict materially with this work order, pause and record the conflict rather than layering a
compatibility branch.

Evidence: exact current paths and changed baseline are recorded in the implementation report; no
pre-existing path is cleaned, reset, stashed, overwritten, or included in Goal-owned changes.

### Gate 1 — Close the typed Observation boundary

Implement the three trigger variants and their strict current durable grammar. Convert new durable
reply turns to DelegateReply, preserve old reads, and make recent/Undo/recall projections distinguish
manual causality from automatic triggers.

Evidence: `PlayerTurnObservationTests`, reply-lease tests, recent display tests, rewind/pop tests, and
cold-reopen/digest tests prove exact timestamp reuse, current round trips, legacy reads, illegal mixed
forms rejected, no synthetic draft restoration, and no change to manual PlayerAction behavior.

### Gate 2 — Close server-side idle admission

Add the process-local cadence state and lifecycle notifications, then extend the existing pulse under
`TurnLock`: reconcile, claim Ready reply first, otherwise claim one due heartbeat activation, otherwise
return status. Ensure start failure cannot leave a phantom claim and terminal auto failure pauses.

Evidence: deterministic `TimeProvider` tests prove first arm, 10-minute due boundary, 30-second sponsor
gap re-arm, reset after every successful origin, no early/catch-up activation, reply priority, busy and
recovery fail-closed, multi-request uniqueness, cross-tab pause protection, restart/session recreation
re-arm, and zero extra starts after an autonomous failure.

### Gate 3 — Close the browser control and state indication

Change the network interval to 10 seconds, update the control copy, validate the new exact response
shapes, attach SSE using returned origin, and render waiting/paused countdown plus last activation.
Keep one recursive timer and one in-flight mutation; keep textarea isolation and read-only
response-loss reconciliation.

Evidence: pure exported decision/formatting helpers plus Node tests cover response validation,
countdown formatting, timer non-overlap, local projection, reply/activation messages, paused state,
ambiguous response fail-closed, terminal error behavior, and no textarea access by the automatic
path. `node --check` passes. Static source-slice assertions alone are not sufficient for new cadence
decisions.

### Gate 4 — Integrate post-Action behavior, docs, and diagnostics

Prove an autonomous turn enters the normal main completion and terminal post-processing path while
bypassing player-only Memo recall. Update the README's Observation, endpoint, browser-loop,
diagnostic, and non-durable scheduling descriptions. Remove stale one-second/204/synthetic-marker
browser claims while preserving the durable supervisor's one-second fallback. Retain the marker only
as the XML-documented internal V1 reply-lease discriminator; remove every claim that it is new reply
player text.

Evidence: provider-free fake-client verticals show one autonomous main call, player recall not called,
ordinary terminal outbound-mail/Character Note hooks retain their existing eligibility, and a
character response that chooses rest is accepted without forced tool use. Source search and docs have
no stale current-contract statements; application logs omit expected waiting-pulse lines.

## 6. Global completion contract

Every in-scope requirement must map to a gate and executable or inspectable evidence, and every
Goal-introduced change must map back to this work order or a clearly documented implementation
necessity. At minimum run from `/repos/focus/atelia`:

```bash
node --check prototypes/Galatea/wwwroot/assets/galatea.js
node tests/Galatea.Server.Tests/galatea-http-v1.test.mjs
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~PlayerTurnObservationTests|FullyQualifiedName~GalateaDelegationRuntimeVerticalTests|FullyQualifiedName~GalateaRecentRewindHostTests|FullyQualifiedName~GalateaSseV1Tests|FullyQualifiedName~GalateaBrowserSponsoredAutonomyPostProcessingTests'
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj -c Release --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'
git diff --check
```

Use `TMPDIR=/mnt/wsl/fast/atelia-test-tmp` if the current environment still provides that verified
directory. Preserve gated live skips and report unrelated baseline failures separately; do not make a
provider call to close this phase.

Update this work order's status to Complete only after code, deterministic tests, current README,
diagnostics, and a requirement-to-evidence audit agree. Do not append a progress transcript. Worktree
closure means all Goal-owned changes are explained and validated while pre-existing changes remain
preserved; a clean `git status` is not required. Stop at the deterministic, provider-free MVP. Live
E2E, durable scheduling, configurable cadence, and higher-level autonomous goals remain future work.

## 7. Completion evidence

- Gate 0: implementation re-established the `main` baseline at
  `51ef1730bd76498dc1c90dfcb0e8cd9fde84f3d7`, preserved the initially clean
  worktree, and introduced no ignored-state or external mutation.
- Gate 1: typed Observation, V1 lease bind/reopen, timestamp, recall, recent,
  rewind, collision, and strict grammar tests pass. Current bind accepts only
  current typed output; historical bytes remain reopen-only.
- Gate 2: deterministic monotonic-clock and HTTP verticals cover first arm,
  exact due, strictly-greater-than-30-second re-arm, no catch-up, reply-first,
  pause/reset, restart, TurnLock uniqueness, and exact claim rollback.
- Gate 3: executable Node fake-timer tests cover exact 200/202 validation,
  cross-generation global single-flight, stale responses, fail-closed stream
  loss, countdown projection, and draft isolation. Rendered HTML tests cover
  the opt-in copy, visible countdown/state/last-activation nodes, ARIA, and
  maintenance/default-off behavior.
- Gate 4: `GalateaBrowserSponsoredAutonomyPostProcessingTests` drives the real
  authenticated HTTP cadence to `HeartbeatActivation`, proves one main call,
  zero Memo recall, exact terminal Action delivery to both outbound-mail and
  Character Note extractors, durable mail capture, default MemoPod write, note
  receipt, and acceptance of a plain no-tool character response.
- Final verification from `/repos/focus/atelia`: Node syntax and behavior
  passed; focused .NET passed 58/58; full Debug and Release each passed 754
  with one existing gated live skip and zero failures; `git diff --check`
  passed. Independent package and cross-package reviews reported no residual
  P0-P2 after tail fixes.
- Live provider E2E, ignored-state mutation, dependency installation, commit,
  push, deployment, durable scheduling, and configurable cadence were not
  performed.

## 8. Pause and escalation

Pause instead of silently changing the accepted design when:

- current source invalidates reply-first admission or single-writer `TurnLock` assumptions;
- typed automatic Observations require a SessionJournal format/schema migration rather than an
  additive readable dialect;
- a successful-turn or terminal-failure boundary cannot be observed without adding a second
  authority;
- deterministic multi-request uniqueness cannot be achieved with process-local state;
- completion requires provider cost, ignored-state mutation, dependency installation, destructive
  cleanup, deployment, push, or another external action;
- evidence expands the work into durable scheduling, delegation settlement, RecapGrid, MemoPod, or
  Character Note semantic redesign.

Record such a discovery concisely in this work order under a clearly labeled blocked note or in a
separate review artifact. Do not rewrite the approved decisions, add a compatibility layer, or call
unfinished work complete.
