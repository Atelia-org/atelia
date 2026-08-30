# MemoPod

This project exposes the Linux-only public `MemoPod` Editable/Frozen lifecycle.
`Create` starts an in-memory Editable Pod without creating storage;
`FreezeAsync` renders and durably commits the complete Pod before entering
Frozen; `Open` strictly reads a committed document and returns a Frozen Pod;
`ResumeEditing` explicitly returns it to the write phase.
The object is a single-owner, sequential orchestration unit and is not
thread-safe.

The entry contract intentionally contains only `Append`, `Remove`, `Get`,
`TryGet`, and snapshot-valued `List`. `Append` accepts exact text plus optional
nullable `Title`, `Gist`, and `Summary` metadata. Metadata is not unique and
does not participate in identity; `MemoId` remains the sole stable address.
There is no in-place update/upsert or mutable Topic/Memo. IDs returned by
Append are provisional until a successful Freeze commits the aggregate;
committed and removed IDs are never reused.

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

Single-owner external-effect reconciliation has two narrow public read/seal
operations. `ComputeStateIdentity()` is available in Editable and Frozen phases
and returns `atelia.memo-pod.document.v2.sha256:<lowercase-hex>`, derived from
the exact canonical complete document candidate bytes. In Editable phase this
is only a working candidate identity; in Frozen phase it identifies the
committed state represented by that valid handle. The identity is not a
snapshot, revision, CAS token, or concurrent-read lease.

`ConfirmCurrentDocumentDurability()` is Frozen-only. After a fresh strict
`Open` has already matched the expected state identity, it validates the
current document path and fsyncs that Pod's exact `memo-pods/v1/pods`
directory. It exists only to close an installed-but-previously-unsynced
recovery window; a normally successful `FreezeAsync` already performed this
directory sync and needs no second confirmation. An indeterminate handle must
still be discarded and reopened before either reconciliation operation can be
used.

Concrete provider configuration and live canary activation are not delivered
here. No prompt, renderer, Store backend, snapshot, detached resolver, or
provider-specific client is part of the public API.
