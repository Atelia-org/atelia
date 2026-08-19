# SessionJournal.MemoPod.DebugApp

This is a Linux-only, fake-first operator for the single-Pod MemoPod
lifecycle. It never loads Completion connection files, environment-backed
credentials, concrete providers, or call logging. Explicit `--live` support is
deferred to Track C2 together with real-provider acceptance and logging/privacy
review.

Every invocation owns one complete phase. `create` and `edit` finish with one
successful `FreezeAsync`; `inspect`, `get`, and `recall` open an already Frozen
Pod. There are deliberately no cross-process `freeze` or `resume` commands.
New IDs are printed only after the Freeze that commits them.

All Topic, Memo, and query text comes from strict UTF-8 files. BOM and invalid
UTF-8 are rejected, and text is neither trimmed nor normalized. `inspect` and
`recall` print only content-free IDs/counts. `get` is the sole command that
writes exact Memo text to stdout, without adding a trailing line feed.

## Cold-start fake workflow

Run from the repository root:

```bash
operator_root="$(mktemp -d)"
input_root="$(mktemp -d)"
printf '%s' 'customer order details' > "$input_root/topic.txt"
printf '%s' 'order 17 ships Friday' > "$input_root/old.txt"
printf '%s' 'find shipping details' > "$input_root/query.txt"

dotnet run --project prototypes/SessionJournal.MemoPod.DebugApp -- \
  create --root "$operator_root" \
  --pod 11111111111111111111111111111111 \
  --topic-file "$input_root/topic.txt" \
  --memo-file "$input_root/old.txt"

dotnet run --project prototypes/SessionJournal.MemoPod.DebugApp -- \
  inspect --root "$operator_root" \
  --pod 11111111111111111111111111111111

dotnet run --project prototypes/SessionJournal.MemoPod.DebugApp -- \
  recall --root "$operator_root" \
  --pod 11111111111111111111111111111111 \
  --query-file "$input_root/query.txt" \
  --fake-return-id m1:00000001

dotnet run --project prototypes/SessionJournal.MemoPod.DebugApp -- \
  get --root "$operator_root" \
  --pod 11111111111111111111111111111111 \
  --memo m1:00000001
```

To replace a committed memo without exposing the provisional replacement ID,
write the new exact text to a file and publish the remove plus append together:

```bash
printf '%s' 'replacement detail' > "$input_root/new.txt"
dotnet run --project prototypes/SessionJournal.MemoPod.DebugApp -- \
  edit --root "$operator_root" \
  --pod 11111111111111111111111111111111 \
  --remove m1:00000001 --memo-file "$input_root/new.txt"
```

The successful `edit` report publishes the new committed ID. If the single
`FreezeAsync` fails, stdout remains empty; reopening observes a complete
old-or-new durable document, never a mixed correction.

The deterministic fake returns exactly the raw strings supplied through
`--fake-return-id`; with no such option it returns an empty selection. The
production `MemoPod.RecallAsync` parser still validates the tool call, IDs,
ordering, hydration limits, and Frozen epoch. The fake does not claim semantic
retrieval quality.
