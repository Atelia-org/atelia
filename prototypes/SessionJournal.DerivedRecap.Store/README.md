# DerivedRecap Store V4

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

Application code reads current-lineage state through
`DerivedRecapLineageView.Capture(store, engine)`. The view verifies the same
repository path and `RefId`, captures at most 513 header-only entries from the
current lineage, and keeps that engine-bound prefix paired with the Store for
all selection and restore inspection calls. Resolving a set admission anchor
captures a second, historical prefix of at most 513 headers starting at that
anchor. Callers cannot inject a public snapshot or prefix.

The bounded result surface fails closed. If the required anchor or strict
ordinal could exist only beyond a truncated prefix, Store returns typed
`BeyondPrefix` evidence containing the required anchor, captured head, header
count, and continuation address; it does not fall back to a full-lineage scan.
Current-lineage Building inventory scans only direct entries and stops after
1,025 observations: at most 1,024 entries are accepted, while an over-cap or
unreadable inventory returns typed `StoreUnavailable`. Staging and malformed
entries do not become semantic Building candidates, but still count toward
that resource bound.

Building creation has the same authority boundary. Application code uses
`DerivedRecapBuildingInstaller`, while direct `CreateBuildingAsync` remains an
internal trusted/test seam. Before any source read or staging write, the
installer validates admission anchor, replay routes, prior-context anchors,
source anchors, and non-retroactive publication against its captured lineage.
Current Building execution supports Empty, Existing, and Inherit sources;
referenced Published sources are reread exactly and frozen into the Building.

`DerivedRecapPublisher.PublishAsync` is also a closed result contract:
`Published`, `NotPublishable`, `BeyondPrefix`, `StoreUnavailable`, or
`RawHeadChanged`. Admission and historical source validation occurs before
staging/sealing writes, and the final raw-head fence remains immediately before
promotion. These authority and resource-bound changes do not change durable
Store, manifest, block, frozen-input, or publication-envelope schemas.

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
