# DerivedRecap Store R0

This project owns event-addressed Recap persistence, structural validation,
atomic publication, strict ordinal selection, and exact materialization.

## Frozen maintainer capability

Every Maintain plan durably freezes the exact
`(MaintainerId, Target, MaintainerCapabilityFingerprint)` triple. The
fingerprint is opaque to Store and must use the canonical
`sha256:<64 lowercase hex>` syntax. Store never derives it from a current
Maintainer or defaults it from `MaintainerId`.

The durable directory and Store header remain `derived/recap/v4` and Store
schema v4. Frozen inputs and final blocks also remain v4. The manifest and
Published publication envelope are schema v5 because their canonical payloads
now commit the capability fingerprint. There is deliberately no v4
manifest/publication reader: an old Building must be explicitly abandoned;
an old Published Store must be explicitly reset and rebuilt.

## Publication authority

Application code publishes through `DerivedRecapPublisher`, which is bound to
one `DerivedRecapStore` and the same `SessionJournalEngine` path/`RefId`.
Callers provide only `SetAdmissionAnchor`; they cannot supply a raw-lineage
snapshot as publication authority.

The publisher captures header-only raw lineage from the engine. While holding
the Store's per-Ref exclusive lock, publication:

1. validates the Building against the captured lineage;
2. seals and durably installs `publication.json`;
3. repeats structural/latest-anchor validation;
4. rereads the bound engine's current head immediately before directory
   promotion;
5. atomically promotes Building with Linux
   `renameat2(RENAME_NOREPLACE)`.

The snapshot-based Store methods are internal diagnostic/trusted seams. R0
`CreateBuildingAsync` executes only Empty Maintain plans. Existing/Inherit
plans have canonical codecs, but their executable exact-source freeze remains
an R1 responsibility.

## Durability evidence

`SessionJournal.DerivedRecap.Store.CrashHarness` is a separate process used by
tests. It terminates itself after named IO/rename points. Parent-process tests
reopen the repository and verify:

- root creation is either unavailable or fully committed;
- sealed Building never counts as Published membership;
- directory promotion has old-or-new visibility;
- reset is either quarantined/unavailable or a fresh committed root.

These tests exercise real process death and Linux filesystem reopen behavior.
They do not claim to simulate physical power loss, volatile device caches, or
filesystem/hardware behavior outside the documented fsync guarantees.
