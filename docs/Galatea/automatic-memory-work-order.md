# Automatic memory closure

## Scope and decisions

Approved implementation following the headless infrastructure pilot. Tracked
code, deterministic tests and contracts only; no live configuration migration,
real provider calls or live journal writes.

1. PlayerAction, HeartbeatActivation and DelegateReply share memory enrichment,
   retaining their actual typed triggers. Inbound and recovery do not perform
   fresh recall. Recovery reuses frozen Observation bytes.
2. Use the existing Memo selector, Title eligibility, RecallBarrier and origin
   barrier. The query is hard-cut to typed-trigger V2; no query-builder LLM.
3. Character Memory SQLite V3 creates a receipt outbox entry atomically with
   each new Planned -> Applied settlement. Existing Applied rows migrated from
   old databases do not receive retrospective notifications. AlreadyApplied
   does not issue another obligation. Receipt eligibility follows the durable
   save fact, even when source Action is no longer the selected head.
4. Delivery states: Pending -> ObservationBound -> Delivered. Binding freezes
   exact base head and complete canonical Observation before SendAsync. Raw
   journal proof of an appended Observation settles delivery; no append rolls
   back to Pending; conflicting/uncertain proof fails closed. This promises
   durable Observation delivery, not provider reception or character cognition.
   Delivered retains receipt/source/base/address but clears the duplicated
   complete Observation payload; SessionJournal remains its durable owner.
5. Reconcile a bound receipt before any abandon/rewind can remove its selected
   lineage evidence. Delivered notifications are never reissued after rewind.
6. Enrich with a pending receipt before optional recall. New reply cutoffs
   reserve one notice slot and the actual receipt envelope budget. Retain the
   total limit of 16 notices and existing whole-Observation budget.
7. Remove the in-process FIFO as a second authority. Full receipt bodies are
   frozen once from durable Memo identities and ExactText. Pathological text
   rendering uses a bounded, truthful identity-only confirmation, not silent
   dropping or claiming metadata/recall completion.

## Work packages

- WP1: typed Observation/query/lease matching and unit contracts.
- WP2: transactional outbox, strict V3 validation/migrations, store tests.
- WP3: runtime admission/enrichment/reconciliation, reservation, removal of FIFO.
- WP4: independent reviews, crash/restart and autonomous vertical tests,
  current documentation and final serial validation.

Shared runtime files are owned by the root agent; workers have disjoint
contract/store/test scopes. Each package receives independent review and tail
fixes before final acceptance.

## Acceptance

- No Player input: automatic save -> next automatic receipt, plus eligible old
  Memo recall. Reply-triggered and Player-plus-reply turns also enrich.
- Same timestamp and final bytes through selector, lease binding and journal;
  recovery never reruns selection or claims another receipt.
- Applied-before-runtime-return and Observation-before-ledger-ack restart cuts.
- Predispatch failure preserves receipt and rolls back reply membership.
- AlreadyApplied/repeated reopen/rewind do not duplicate delivery.
- Sixteen ready replies plus pending receipt stay bounded and make progress.
- Disabled recall has no selector/context I/O; receipt delivery remains active.
- Existing exception priority, shutdown drain, source and recall barriers hold.

Final commands: serial Debug/Release Galatea tests (`--no-restore -m:1 -nr:false`),
Release server build, Node HTTP/SSE/follower tests, SessionJournal docs checker,
and `git diff --check`.

## Implementation and review

All four packages are implemented. Principal changes are `a975ac23` (typed
Observation/query/planner/lease contracts), `c2415b40` (transactional V3 outbox)
and `b4fa7c2f` (runtime enrichment, exact raw-proof reconciliation and capacity
reservation). Separate commits carry vertical tests and review fixes.

Independent contract, store, runtime and documentation reviews are complete.
The following findings were closed, not deferred:

- Bound receipt proof must verify canonical Observation and exact receipt body,
  not merely that some Observation was appended.
- V2 schema migration accepts the same normalized whitespace language as its
  strict reader, revalidates under BEGIN IMMEDIATE, and preserves the active
  transaction when validation helpers create commands.
- Delivered rows discard redundant whole-Observation payloads.
- Sixteen Ready replies reserve a slot/bytes for the receipt and continue with
  the remaining reply in the next cutoff. Seventeen pending saved batches do
  not encounter the removed FIFO's former drop limit.
- Automatic vertical tests use real reply leases, canonical MemoIds and legal
  raw rewind APIs. Post-turn raw assertions hold TurnLock, just as runtime
  materialization does, avoiding a race with the DerivedInfo pump.
- Release validation exposed an existing fatal-runner test resolving a service
  after the host had already disposed its DI container. The test now captures
  the same ApplicationStopping token before dispatch; all failure, lock,
  transport and disposal assertions are preserved. Production code is unchanged.

## Deployment boundary

No live config, session history or ignored Character Memory database was opened
or migrated for this work; no real provider was called and no push was made.
Before deploying, stop the server, confirm no state-directory owners, run the
existing backup script, then allow writable attach to validate/migrate old
Character Memory databases. Old Applied batches do not receive retroactive
receipts; unfinished batches first settled after upgrade do.

The next live gate is a no-page soak covering automatic save, subsequent
receipt delivery and eligible Memo recall, followed by clean stop/reopen. This
work does not implement endogenous goals, adaptive wake intervals or automatic
uncertain-completion recovery.

## Final verification

- Debug full suite: 822 passed, 1 skipped, 0 failed (823 total).
- Release full suite: 822 passed, 1 skipped, 0 failed (823 total).
- After the final test-only fatal-lifetime fix, Debug endpoint-lock/autonomy
  regression groups were rebuilt and rerun: 15 passed, 0 failed.
- The skipped case is the explicitly opt-in real Codex provider test.
- Release server build: 0 warnings, 0 errors.
- Node HTTP/SSE/Agent follower: 13 passed, 0 failed.
- SessionJournal docs checker: 21 files, 0 diagnostics.
- `git diff --check`: clean.

```sh
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false -v:minimal
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --configuration Release --no-restore -m:1 -nr:false -v:minimal
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false -v:minimal --filter 'FullyQualifiedName~GalateaEndpointLockTopologyTests|FullyQualifiedName~GalateaAutonomyPostProcessingTests'
dotnet build prototypes/Galatea/Galatea.Server.csproj --configuration Release --no-restore -m:1 -nr:false -v:minimal
node --test tests/Galatea.Server.Tests/galatea-http-v1.test.mjs tests/Galatea.Server.Tests/galatea-sse-v1.test.mjs tests/Galatea.Server.Tests/galatea-agent-follower.test.mjs
python3 scripts/check_session_journal_docs.py
git diff --check
```
