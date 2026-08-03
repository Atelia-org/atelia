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
schema v4. Final blocks also remain v4. The manifest and Published publication
envelope are schema v6, and frozen inputs are schema v5. Their canonical
payloads freeze the exact governing setup references for admission, source
cursor, replay start, and every catch-up boundary. Maintain routes use
`RecapReplayBoundary(Address, Setups)`, and the final boundary must exactly
equal both manifest admission fields. There are deliberately no readers for
manifest/publication v5 or frozen-input v4: an old Building must be explicitly
abandoned; an old Published Store must be explicitly reset and rebuilt.

## Publication authority

Application code publishes through `DerivedRecapPublisher`, which is bound to
one `DerivedRecapStore` and a `SessionJournalReadView` with the same repository
path/`RefId`. The read view is valid only for the lifetime of its owning
`SessionJournalEngine`.
Callers provide only a metadata-issued `BuildingPlanHandle`; they cannot supply
a raw-lineage snapshot as publication authority. Frozen execution paths call
`Prepare(handle, expectedRawHead)` before component work. This returns an opaque
`PreparedRecapPublication` bound to that exact handle, Publisher, captured
lineage, admission-relative lineage, and caller-frozen head. Its
`CanPublishAsync`/`PublishAsync` overloads consume the prepared authority without
recapturing lineage after component work. The public diagnose/publish surface
accepts only this prepared authority; handle-only convenience overloads are an
internal trusted seam so a Host cannot accidentally capture publication
authority after provider or component side effects.
If the owning `SessionJournalEngine` is already disposed when one of these
engine-bound views or prepared authorities is called, it fails deterministically
with `ObjectDisposedException` before cached results or Store I/O are used.

The publisher captures header-only raw lineage from the read view. While holding
the Store's per-Ref exclusive lock, publication:

1. validates the Building against the captured lineage;
2. seals and durably installs `publication.json`;
3. repeats structural/latest-anchor validation;
4. rereads the bound read view's current head immediately before directory
   promotion;
5. atomically promotes Building with Linux
   `renameat2(RENAME_NOREPLACE)`.

Application code reads current-lineage state through
`DerivedRecapLineageView.Capture(store, engine.ReadView)`. The view verifies
the same repository path and `RefId`, captures at most 513 header-only entries
from the current lineage, and keeps that engine-lifetime-bound prefix paired
with the Store for all selection and restore inspection calls. Resolving a set
admission anchor captures a second, historical prefix of at most 513 headers
starting at that anchor. Callers cannot inject a public snapshot or prefix.

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
promotion. The Store directory/header and final-block schemas remain v4; the
frozen wire cutover above intentionally changes manifest, frozen-input, and
publication schemas without a compatibility layer.

Online installer and Planner execution paths use explicit bounded-prefix proof
for raw anchors, setup authority, and planning windows. They return typed,
stage-qualified `BeyondPrefix` before component payload reads, Maintainer calls,
or Store writes; they do not fall back to full-lineage header or governing-setup
discovery.

Published restore separates metadata proof from component mutation. Exact
inspection issues opaque per-block write authorities bound to the Store,
restore handle, block, checkpoint state, and final state. Successful writes
refresh that authority. The Store then issues one opaque envelope-commit
authority only from a complete roster for the same exact handle. Public writes
and envelope commit do not accept caller-supplied state-token maps; final
commit rechecks the raw head and exact component identities and cannot return
`BeyondPrefix`.

Building content inspection likewise requires a metadata-issued
`BuildingPlanHandle`. The handle is bound to normalized repository path plus
`RefId`: it remains valid across reopening that same durable Store identity,
but is rejected by a Store opened for another path or RefId.

The final raw-head reread is a fence, not an atomic compare-and-swap with the
raw journal. The SessionJournal engine lock excludes other engine processes for
the lifetime of the engine, and the per-Ref Store lock excludes cooperating
Store operations across processes. Neither lock serializes two callers sharing
the same engine instance: such callers must serialize raw mutation against
Building install, Publish, and Restore themselves. A raw mutation racing after
the final reread is therefore outside the Store's CAS guarantees.

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
