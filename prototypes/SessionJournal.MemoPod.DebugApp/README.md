# SessionJournal.MemoPod.DebugApp

This is a Linux-only, fake-first operator for the single-Pod MemoPod
lifecycle. The default path never loads Completion connection files,
environment-backed credentials, concrete providers, or call logging. Track C2
adds a separately gated `recall --live true` path; it is a candidate runner,
not production activation or proof that the route is compatible.

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

## Provider-free live source gate

The live source slice is covered without network access. Its composition test
passes a Frozen Pod through `RecallAsync`, the real DeepSeek V4 OpenAI Chat
converter, fake HTTP/SSE, normalized cache usage, and Memo hydration. The
current converter emits all of the following:

- `thinking.type=disabled`;
- `stream_options.include_usage=true`;
- the required named `recall_memos` tool choice;
- `parallel_tool_calls:false`.

The fake HTTP handler also observes the current exact target as
`POST https://api.deepseek.com/v1/chat/completions`. The official current base
root and Chat Completion path are `https://api.deepseek.com` and
`POST /chat/completions`; both the local `/v1/chat/completions` path and the
last field above are only current local wire facts. Their acceptance by the
live DeepSeek route remains unknown until an authenticated canary succeeds.

## Authenticated candidate runner

Live execution is Release-only and fail closed. Before loading a connections
file or constructing a Completion client, set both logging sinks to `ERROR`:

```bash
export ATELIA_DEBUG_FILE_LEVEL=ERROR
export ATELIA_DEBUG_CONSOLE_LEVEL=ERROR
```

Use a disposable working directory and a disposable synthetic Pod. Do not use
a user Pod or a working directory whose `.atelia/debug-logs` contains retained
production diagnostics. The strict V1 connections file must select one exact
ID with this policy:

- `kind`: `openai-chat`;
- `modelId`: `deepseek-v4-flash`;
- `completionSurfaceId`: `openai-chat/deepseek-v4`;
- `reasoningEffort`: `disabled`;
- origin: exactly `https://api.deepseek.com/` (no userinfo, alternate port,
  path, query, or fragment);
- credentials: nonblank `apiKeyEnv`; the candidate route policy rejects an
  inline-only `apiKey` source.

The runner uses `TryGet` followed by `GetClient`; an unknown requested ID never
falls back to `defaultConnectionId`. It does not use `LoggingCompletionClient`
or an HTTP exchange sink, does not retry, and treats repeated `--query-file`
arguments as explicit separate provider calls.

The current shared client resolves the locked origin to
`POST https://api.deepseek.com/v1/chat/completions`. An authenticated canary
must accept that exact path and `parallel_tool_calls:false`; source tests do not
claim either is live-compatible.

Example shape (the referenced files and environment variable must be created
by the operator; never commit their contents):

```bash
dotnet run -c Release \
  --project prototypes/SessionJournal.MemoPod.DebugApp -- \
  recall --live true \
  --root "$disposable_pod_root" \
  --pod 11111111111111111111111111111111 \
  --connections "$disposable_connections_v1" \
  --connection deepseek-v4-flash-recall \
  --case cold-01 \
  --query-file "$synthetic_query_1" \
  --query-file "$synthetic_query_2" \
  --max-prompt-bytes 33554432 \
  --max-tokens 256 \
  --delay-ms 0
```

`--case` is 1–64 lowercase ASCII letters/digits plus `.`, `_`, and `-` after
the first character. Live mode accepts 1–8 query files, prompt bytes in
1–33,554,432, max tokens in 1–4,096, and delay in 0–30,000 milliseconds. Fake
arguments and live arguments are mutually exclusive.

Each attempted provider call writes one content-free JSONL evidence record.
It contains route identifiers, Pod/active counts, the fixed
`frozenPromptFormatId=atelia.memo-pod.prompt.v1`, prompt hash/bytes, query bytes,
bounds, delay, elapsed time, outcome, normalized cache status/token fields, and
selected Memo IDs. It never contains Topic, Memo/query text, system
prompt, raw request/response, command arguments, diagnostics, exception text,
endpoint configuration, or credentials. The live runner computes the shared
Frozen Observation hash/UTF-8 length transiently, clears its temporary byte
buffer, and checks the hash against `MemoRecallResult.FrozenPromptSha256`.

The tracked candidate record starts at `NotRun`:
[`memo-pod-deepseek-v4-flash-candidate.md`](../../docs/SessionJournal/evidence/memo-pod-deepseek-v4-flash-candidate.md).
