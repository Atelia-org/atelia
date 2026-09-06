# Galatea headless Agent infrastructure pilot

状态：tracked infrastructure pilot 已完成，provider-free 验收通过。用户已批准 tracked source、tests、docs 与独立 commits；live 启用与 soak 留待后续阶段。

## 目标与边界

Galatea.Server 在没有浏览器和 Player 输入时，为静态 enrollment 的用户续接 Ready Codex 回信，并在主轮次完成后空闲十分钟时激活角色。浏览器成为观察与人工诊断面板。

- Root config hard-cut 为 V8，新增 optional `serverAgentUserIds`，省略或 `[]` 表示全部禁用。列表成员必须 exact 命中已配置 user，Ordinal unique；null、未知成员与错误类型拒绝。Bootstrap 写 `[]`。
- 自动轮次只使用该 user 的 `defaultConnectionId`；配置在启动时加载，不支持 enrollment CLI override 或 hot reload。
- 非 maintenance 仅 eager attach 已 enrollment session。Maintenance 保留配置校验，但不为 Agent loop attach session。
- 固定十分钟 monotonic idle cadence，十秒后台检查，busy 跳过。启动重新 arm，不补停机期间 tick；删除 browser sponsor continuity。
- Ready reply 优先；Heartbeat failure 暂停空激活，仍可处理 Ready reply。非 Idle recovery boundary 阻止自动推进；不自动 abandon、resume 或 restart uncertain completion。
- DelegateReply failure 即使 raw 回到 Idle，也必须以 process-local blocked latch 阻止重新领取同一 Ready reply；后续成功 main turn 清除，重启重新检查 durable boundary。此 latch 在 `TurnLock` 内 settlement，不依赖浏览器错误处理。
- 保留 SessionJournal、SQLite reply lease、typed Observation、post-processing、StopController 与 exact recovery identity。
- 不新增 TextExtractor wake directive、可调间隔、durable scheduler、目标引擎或运行时 admin 权限系统。
- 本轮不修改 ignored live config/state，不启用真实 provider soak，不 push。后续启用需要停服、备份、显式迁移 V8 和 bounded live soak。

## 单一执行路径

`GalateaAcceptedTurnRunner` 统一 chat、recovery、inbound、automatic 的后台执行、异常映射、terminal settlement、recent refresh 与 writer-lock release；HTTP DTO 构造留在 endpoint。

`GalateaAutomaticTurnCoordinator` 拥有 automatic admission：enrollment/maintenance/lifecycle gate → session → `TurnLock.Wait(0)` → durable reconcile → exact Idle → user default → fresh admission → reply cutoff first → cadence claim → runner handoff。返回 typed result，内部完成 handoff 前的 lease/claim 补偿。

`GalateaServerAgentHostedService` 启动时 attach enabled sessions，后台直接调用 coordinator，观察运行 task 并在关停时 drain。先禁止新 admission，再取消并等待 scheduler/已启动任务，最后释放 session 与其依赖。

`POST /api/v1/mailbox/ready-turn` 降为 enrolled user 的一次手动检查，strict `{}`，不强制越过 cadence。新增只读 Agent 状态接口，读状态不 attach session、不执行 admission。浏览器删除自动 POST 和 checkbox，通过只读状态/current/recent/SSE 跟随服务端轮次，保留草稿隔离。

Review 发现新增后台 writer 后需要一起收口：关停必须阻止新的 session attach，并排空已接纳的 attach；fatal turn task 必须被宿主观察；页面需要持续 GET-only follower 才能看到加载完成后新启动的后台轮次。

## 工作包与验收

1. WP1 runner：四个 HTTP 入口复用，外部行为保持；success、nonfatal、fatal、shutdown 与锁释放验证。
2. WP2 coordinator：共享 automatic admission，HTTP 成为 adapter；reply-first、busy、recovery、handoff rollback 验证。
3. WP3 config/host/cadence：V8 静态 enrollment、only-enabled attach、server timer、shutdown drain；十分钟边界、长轮次、restart/no catch-up、failure pause 与连接选择验证。
4. WP4 Dev 面板：浏览器只读跟随，移除 sponsor 代码/DOM；runtime JS、strict status decoding、manual draft isolation 验证。
5. WP5 集成：当前 README/contracts 同步，独立 review 与尾修，串行 Debug/Release Galatea suites、Node tests、Release build、文档检查、`git diff --check`。

每包在明确写入范围内实施并独立提交。`Program.cs`、`GalateaServices.cs`、共享 test host 按区段/依赖串行；接口稳定后允许前端与文档并行。

## 验证记录

- V8 configuration：`ee2aaa21`；config/root-language/provisioning focused 93/93。独立 review 无正确性 findings；说明 runtime record 可被 initializer 覆盖，host 因此再次校验并复制快照。
- WP1 runner：`16dc5e1d`；在隔离 worktree 验证 EndpointLockTopology/HTTP V1/recent focused 73/73。独立 review 无 findings。
- WP2/WP3：`7a4eb997`；coordinator、server cadence、HostedService 与 shutdown 生命周期合包收口。原测试迁移 `aacab2b2`，失败回信回归 `51bf58f2`，状态纯读契约 `30fca738`。
- WP4：`1cfe05e7`；GET follower、显式恢复按钮与 DOM/fake-timer 测试。独立 review 发现初始化 GET 竞态与过时 recovery 文案，均在该包修复。
- 集成尾修：`6cd22db2` 和 `ef8157be` 将旧 disposal/HTML 断言迁移到新契约。Host disposal 为单次 task，重复等待保留同一异常；fatal 清理仍释放余下依赖后传播原因。
- 完整 Debug：792 passed、1 gated live test skipped、0 failed。
- 完整 Release：792 passed、1 gated live test skipped、0 failed。
- Node HTTP/SSE/Agent follower：13/13 passed。
- Release build：0 warnings、0 errors。
- 文档检查：21 files、0 diagnostics；`git diff --check` 通过。
- Runtime、frontend、config 与最终 docs/runtime 一致性均经独立 review，无未解决正确性 findings。

本轮新增真实 HostedService 的 provider-free 垂直测试：不创建 HTTP client/Player 输入，fake timer 驱动空闲激活；配置两个用户，仅 enrollment 的用户 attach，且使用非首个 selectable default。还验证 long gap 不补跑、pre-dispatch reply failure 不重领、状态 GET 纯读，以及 WAF → Hosted.Stop → Host.Dispose 在已有 writer 时确实等待其排空。测试中发现 fake provider descriptor 的 ApiSpecId 不一致，已修复 fixture；先前 WAF disposal 提前返回的问题通过明确 Hosted.Stop owner 和幂等 disposal 收口，并保留完整宿主回归。

最终验证命令（.NET 串行执行）：

```sh
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false -v:q
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --configuration Release --no-restore -m:1 -nr:false -v:q
dotnet build prototypes/Galatea/Galatea.Server.csproj --configuration Release --no-restore -m:1 -nr:false
node --test tests/Galatea.Server.Tests/galatea-http-v1.test.mjs tests/Galatea.Server.Tests/galatea-sse-v1.test.mjs tests/Galatea.Server.Tests/galatea-agent-follower.test.mjs
python3 scripts/check_session_journal_docs.py
git diff --check
```

## 后续 live 启用

部署时先确认实际配置路径和无进程持有状态目录，执行现有 `gitignore/backup-galatea.sh`，显式迁移本地配置到 V8，选择 `serverAgentUserIds:["gpt"]`，保留该 user 的 `defaultConnectionId`。Tracked bootstrap 的默认值仍为空集合。

记录 gpt/cyber 的 SessionJournal head/frontier 与 SQLite integrity，关闭页面运行至少两个完整 idle cadence 周期；确认自动轮次使用 gpt 的 default、Ready reply 优先、cyber 原始状态不变，再正常停服、strict reopen 与复查 integrity。轮次可能持续较长，验收以观察到完整周期为准。此步骤会调用真实 provider 并修改 live history，本轮没有执行。

此pilot证明基础设施可以脱离页面运行。角色动机、automatic Memo recall/Note receipt闭环、TextExtractor唤醒意图、进程外重启保障仍是独立后续工作。

后续进展：automatic Memo recall 与 durable Note receipt 闭环已由
[Automatic memory工作包](automatic-memory-work-order.md)实施；其余上述方向不在该工作包范围内。
