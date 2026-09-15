# Galatea Codex delegation operator recovery gate

> Status: the narrow completed-turn command is implemented. No live recovery is
> performed merely by having the command available; each `--apply` remains a
> separate operator-authorized action after backup and dry-run.

普通 Codex 进程崩溃、线程缺失或历史检查失败由运行时自动有限恢复。当前 wire V6 / delegation SQLite V5 的规则见[运行时](runtime.md)与[结构化输入方案](structured-input-rendering-design.md)；有限恢复政策沿用[恢复方案](codex-delegation-recovery-refactor-plan.md)。运行时不读取 Codex 私有 SQLite 或 rollout JSONL。

## 识别当前状态

- `backoff` / `accepted-history-unavailable`：连续恢复失败尚未达到 8 次；等待正常调度即可。这里的次数不是模型调用次数。
- 回信含 `RESULT_UNCONFIRMED`：本地等待已结束，旧工作可能仍执行或已经留下部分改动；角色应核查工作目录。后续邮件可继续，不自动重发原任务。
- 回信含 `NOT_DISPATCHED_RETRIES_EXHAUSTED`：已确认未发送，但有限恢复未成功；修正环境后可由角色重新委派。
- `quarantined` / 无法打开数据库：检查受限错误码、所有者、格式、锁和备份；不要把删除数据库作为恢复步骤。

inbox 容量被占满时需正常消费已有回信；后台保留待结算邮件，不通过丢信绕过容量。重启保留失败计数，不能用反复重启重置预算。

## SQLite V5 离线升级

代码升级后，已有 V1/V2/V3/V4 store 必须显式升级；普通启动不会自动改写旧库。V3→V4 增加角色邮件附表，不将历史 `Unrouted` 重新解释为待投递信；V4→V5 增加内容来源、机读绑定与实际发送承诺，保留旧 Task 和 Bound 原文。使用已转换的 V10 配置，先停服并确认 writer lock 已释放，然后执行：

```bash
dotnet run --project prototypes/Galatea/Galatea.Server.csproj -- \
  operator upgrade-delegation-store \
  --config /absolute/path/to/config.json --character exact-character
```

默认 dry-run。确认目标、备份位置和诊断后，在明确的部署窗口用相同命令追加 `--apply`。命令持有原生命周期锁，先备份，再事务迁移并严格重开；重复执行当前格式返回 `AlreadyCurrent`。保持原 baseline/frontier、capture、邮件 ID、terminal 和 notice/lease 事实；旧 Started/Unknown 不凭空获得未发送证明。同步部署 C# 和重新构建的 Node V6 sidecar，不能混用旧 wire。

本次代码与模拟测试不等于真实用户库已迁移，也不等于 live provider 验证。首次恢复后检查失败回信是否正常消费、下一任务是否推进；保留升级备份。

## 可选的人工完成证据恢复

以下命令只用于仍未结算、且有明确完成证据的旧 Accepted 任务。已经本地不明终结的邮件不覆盖成成功；首个持久终结生效。不要仅因短暂历史不可见启动人工操作。人工取证/写入需要限定到具体用户和任务；不由普通自动恢复隐式触发。

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
- **Manual state replacement:** remains separate from the bounded runtime recovery path; this completed-evidence command does not perform it.

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
A strict post-readback must match that complete transition. A rerun whose
durable dispatch/thread/turn identity, final hash/body, and notice all match
returns `AlreadyApplied` without a write. The task digest is validated exactly
before the first apply; after terminalization Galatea intentionally erases the
task body, so a no-write terminal rerun cannot independently validate `task*`
again and does not claim that it can. Malformed/mismatched evidence, a
conflicting terminal state, or a held lifetime lock refuses without invoking
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
