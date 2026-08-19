# SessionJournal.MemoPod

This project exposes the Linux-only public `MemoPod` Editable/Frozen lifecycle.
`Create` starts an in-memory Editable Pod without creating storage;
`FreezeAsync` renders and durably commits the complete Pod before entering
Frozen; `Open` strictly reads a committed document and returns a Frozen Pod;
`ResumeEditing` explicitly returns it to the write phase.
The object is a single-owner, sequential orchestration unit and is not
thread-safe.

The entry contract intentionally contains only `Append`, `Remove`, `Get`,
`TryGet`, and snapshot-valued `List`. There is no in-place update/upsert or
mutable Topic. IDs returned by Append are provisional until a successful
Freeze commits the aggregate; committed and removed IDs are never reused.

Frozen Pods cache an internal deterministic provider-neutral prompt. The cache
is invalidated by `ResumeEditing` and rebuilt by every Freeze, including a
clean refreeze that does not rewrite the durable document. An indeterminate
commit invalidates the current handle; callers must discard it and `Open` the
strict durable authority again.

The publisher rejects existing symbolic-link/reparse-point path components and
publishes only through a same-directory temporary file. This is a non-hostile
single-owner contract: it does not claim resistance to an attacker racing path
replacement between checks and filesystem operations. Readers open only the
exact mapped final document and never enumerate or promote temporary remnants.

Frozen Pods support one provider-neutral `RecallAsync` operation through
`ICompletionClient`. It sends exactly one shared corpus observation and one
query tail, requires exactly one `recall_memos` tool call, validates canonical
active IDs, and hydrates immutable Memo values before the Frozen epoch can be
left. Provider failures, malformed model output, local byte caps, and caller
cancellation remain distinct outcomes; no automatic retry occurs.

Concrete provider configuration and live canary activation are not delivered
here. No prompt, renderer, Store backend, snapshot, detached resolver, or
provider-specific client is part of the public API.
