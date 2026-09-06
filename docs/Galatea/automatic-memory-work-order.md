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
and `git diff --check`. Results will be appended after implementation.
