# Galatea Codex delegation operator recovery gate

> Status: procedure only; no live recovery has been authorized or performed.

This runbook is the separate, operator-authorized gate for a durable Galatea mail
whose Codex turn cannot be reconciled through the supported app-server history
APIs. Normal runtime behavior is defined by
[`codex-delegation-durability-design.md`](codex-delegation-durability-design.md).
The runtime never reads Codex rollout JSONL or private SQLite state.

## Preconditions

Do not begin merely because an `Accepted` turn is temporarily invisible. The
runtime keeps that mail active and retries with durable backoff. Recovery requires
separate explicit authority for the exact user, dispatch, and intended action.

Before any write:

1. Stop Galatea and its durable sidecar, then verify that their process tree is
   gone. Stop any other process that owns the exact state to be backed up or
   changed; an unrelated editor-owned app-server is not by itself evidence that
   Galatea is still running.
2. Verify that the Galatea delegation writer lock and all relevant SQLite/journal
   files have no live holder. A lock file's existence alone is not proof of a
   holder.
3. Make separate, timestamped backups of the exact Galatea user state and the
   exact Codex state involved. Record a manifest and checksums, publish each
   backup atomically, and test that it can be listed/read before proceeding.
4. Record the durable Galatea state, its exact accepted thread/turn identities,
   and the supported app-server read result. Keep mail bodies, final text,
   credentials, and private rollout paths out of tracked documentation and
   ordinary logs.

## Decision and execution

Choose one recovery action explicitly; do not combine them implicitly:

- **Apply proven terminal evidence:** only when the exact dispatch, thread, turn,
  task identity, terminal status, and bounded final/failure evidence agree.
- **Quarantine:** when identity or terminal evidence conflicts and automatic
  settlement would be unsafe.
- **Rebind/abandon:** changes fixed-thread continuity and requires its own design
  and authorization. It is not a fallback for history invisibility.

Use a reviewed Galatea-owned operator command that reuses production validation
and the existing terminal transaction/CAS. If no such command exists for the
chosen action, implement and test that narrow command first. Never edit Galatea
SQLite directly, copy a final into a notice row, reset the mail to `Queued`, or
call `turn/start` again. Raw Codex rollout or private SQLite may be inspected by
an authorized operator as forensic evidence, but must not become a runtime
reader, parser, or second durable ledger.

## Verification and restart

With processes still stopped, reopen the Galatea store through its strict reader
and verify that exactly the intended state transition and notice occurred. Keep
the backups until the user-visible result and store integrity are confirmed.
Only then may a separately authorized restart/E2E proceed. Verify that:

- the recovered dispatch caused zero additional `turn/start` calls;
- at most one terminal notice exists and ordinary ready-turn behavior remains
  one-shot;
- the fixed route still points at the intended thread unless rebind was the
  separately approved action;
- unexpected evidence stops the procedure and preserves the backups.

Restoring a backup is itself a destructive state replacement: stop all holders,
verify the selected archive and target identities again, and obtain explicit
authority before restoration.
