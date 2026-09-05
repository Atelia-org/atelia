# Galatea Codex delegation operator recovery gate

> Status: the narrow completed-turn command is implemented. No live recovery is
> performed merely by having the command available; each `--apply` remains a
> separate operator-authorized action after backup and dry-run.

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

For **Apply proven terminal evidence**, use the Galatea-owned offline command:

```bash
dotnet run --project /absolute/path/to/prototypes/Galatea/Galatea.Server.csproj -- \
  operator recover-codex-completed \
  --config /absolute/path/to/config.json \
  --evidence /absolute/path/to/completed-evidence.json
```

This is a dry-run unless the exact final `--apply` flag is present. Both paths
branch before `WebApplication.CreateBuilder`, so they do not bootstrap config,
start the web server, construct providers, or spawn the sidecar. Config and
evidence arguments must be absolute no-follow regular-file paths on Linux. The
command takes the same per-user lifetime lock as normal Galatea; a live holder
causes refusal. The command enforces evidence mode `0400` or `0600`; create it
under `umask 077` or run `chmod 600 /absolute/path/to/completed-evidence.json`
before dry-run. The final reply is base64 inside that file and must never be
placed in argv or copied into ordinary logs.

The evidence is one closed, strict-UTF-8 JSON object. V1 has exactly these
fields and no others:

```json
{
  "v": 1,
  "kind": "codex-turn-completed",
  "userId": "exact Galatea user ID",
  "dispatchId": "exact durable dispatch ID",
  "threadId": "exact accepted Codex thread ID",
  "turnId": "exact accepted Codex turn ID",
  "taskUtf8Bytes": 1,
  "taskSha256": "64 canonical lowercase hex characters",
  "finalUtf8Bytes": 1,
  "finalSha256": "64 canonical lowercase hex characters",
  "finalUtf8Base64": "canonical padded base64 of the exact final UTF-8 bytes"
}
```

The byte counts and SHA-256 values are over the exact bytes, with no newline,
trim, Unicode normalization, or re-encoding added. `task*` describes the exact
task already stored in the active Galatea mail; `final*` describes the decoded
`finalUtf8Base64`. Derive this file only from separately verified forensic
evidence for the exact completed turn. Raw Codex rollout or private SQLite may
be inspected by an authorized operator while producing that evidence, but the
command itself never reads either and neither becomes runtime authority.

Dry-run strict-opens the Galatea store read-only and requires all of the
following: the configured owner and capacity policy still match; the route is
`Bound` to the evidence thread; its exact active dispatch is `Accepted` with
the same requested/accepted thread and turn; its latest durable reconciliation
code is `ACCEPTED_TURN_NOT_VISIBLE`; no notice exists for it; the task bytes and
hash match; and the final fits the configured reply bound. It prints identities,
outcome, and store revision, but never the task or final.

After a successful dry-run, repeat the same command with `--apply` appended.
Apply reopens and revalidates the exact state under the exclusive lifetime lock,
then calls the production `RecordCompletedMail` transaction. That single CAS
changes only the exact mail to `TerminalCompleted`, creates its exact `Ready`
reply notice, and clears the route active dispatch; queued mail remains untouched.
A strict post-readback must match that complete transition. Repeating exact
evidence returns `AlreadyApplied` without a write. Malformed/mismatched evidence,
a conflicting terminal state, or a held lifetime lock refuses without invoking
the terminal transition; in particular it cannot trigger the store's terminal
conflict quarantine path.

Never edit Galatea SQLite directly, copy a final into a notice row, reset the
mail to `Queued`, or call `turn/start` again. Quarantine and route rebind remain
different operator actions and are not implemented by this command.

## Verification and restart

The command performs an immediate strict post-readback, but keep processes
stopped and independently rerun the dry-run to confirm `AlreadyApplied`. Keep
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
