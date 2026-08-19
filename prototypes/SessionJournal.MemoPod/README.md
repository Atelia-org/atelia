# SessionJournal.MemoPod

This project currently contains foundation pieces only. It does not yet expose
the public Editable/Frozen lifecycle planned for WP-04.

The present domain surface provides strict `MemoPodId` / `MemoId` values,
immutable `Memo` values, and an internal working aggregate with only
Append/Remove/read operations. IDs returned by Append are provisional until a
later successful lifecycle Freeze commits the aggregate; removed IDs are never
reused.

WP-02 adds an internal canonical V1 document codec and Linux-only durable
publisher. These remain test/harness-facing foundations and are not a public
Create/Open/Freeze API.

The publisher rejects existing symbolic-link/reparse-point path components and
publishes only through a same-directory temporary file. This is a non-hostile
single-owner contract: it does not claim resistance to an attacker racing path
replacement between checks and filesystem operations. Readers open only the
exact mapped final document and never enumerate or promote temporary remnants.
