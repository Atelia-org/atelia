# Galatea root config V12（当前合同）

此页定义当前 `config.json` 的 strict V12 root contract。正常 Galatea host 只接受整数
`"v": 12`；未知、重复、缺失字段，以及旧版、未来版、`12.0`、`null`、注释、尾随逗号或尾随数据均
fail closed。实现 owner 是
[`GalateaStrictConfigReader`](../../../../prototypes/Galatea/GalateaStrictConfigReader.cs) 与
[`GalateaRootFileConfig`](../../../../prototypes/Galatea/GalateaConfig.cs)。

[V11](galatea-root-config-v11.md)是历史合同；从 V11 到 V12 必须使用显式 operator upgrade，正常启动不迁移或重写配置。

## Root shape

根字段严格为 `v`、`characters`、`players`、`runtime`。`characters` 为 1..256 项，`players` 为 0..256 项；其余
Character、Player、`listenUrls`、`callLogDir`、`maintenanceMode` 和 `completionAttemptTimeoutSeconds` 的 V11 语义不因本次
RecapGrid 配置收口而改变。相对路径仍以 `config.json` 所在目录解析；strict 文件读取要求 Linux no-follow regular file 语义。

`runtime.recapGrid` 是必需的 exact object，且严格只有：

```json
{
  "maintenance": {
    "connectionId": "local",
    "maximumConcurrency": 1,
    "dispatchTimeoutMilliseconds": 900000
  },
  "historicalAgentControlProfileFiles": []
}
```

`maintenance.connectionId` 必须为非空 string；`maximumConcurrency` 为 1..1024 的整数；
`dispatchTimeoutMilliseconds` 为 1..86,400,000 的整数。`historicalAgentControlProfileFiles` 必须是
0..256 个非空 string path，加载后必须是存在的 no-follow regular file，canonical path 不得重复。

## Live maintenance 与恢复边界

V12 删除 root config 的 live `routeManifestPath`、`agentControlProfileFiles` 与
`currentAgentControlProfileId` authority。maintenance connection、retry invoker、connection registry 与全局并发 lane
共享；每个 work 不得另建 semaphore。asset/default 仅选择尚无 `RowWork` 的新工作；Host 根据已持久化 `RowWork` 的
actual family、protocol 与 semantic key 在执行时构造 exact route，因而已有行按 actual producer 验真，不因当前 default 或
active recipe 变化阻断。普通策略变化不改写 `ActiveRecipeDigest`，新默认 family 与旧未完成 family 都能按其自身事实运行。

已完成 Recap 的读取不依赖 route manifest 或可用 maintenance connection。maintenance connection 不可用时，只在需要新生成
时返回具体阻塞。独立 SessionJournal CLI 的 exact route manifest 仍是其显式 build/operator workflow 的输入；保留它不等于
Galatea root config 继续接受 live route manifest。

`historicalAgentControlProfileFiles` 可为空。非空文件只用于已冻结、需要 exact tool recovery 的历史 profile bytes/identity；
它们不提供新 work 的 admission 或默认 profile authority。fresh missing-session bootstrap 是 host 对 code-owned bundle 的窄入口：
它建立必要的 Store、asset 与 recipe，但不读取历史 profile、不创建 Completion client、不调用 provider，也不会为新 session
创建 Agent Control 工具或扩展旧 profile 的 family allowlist。

## V11 → V12 operator upgrade

唯一入口为：

```text
Galatea.Server operator upgrade-recap-grid-config-v12 --config <absolute-path>
  [--maintenance-route-index <index>] [--apply]
```

默认是 provider-free dry-run。它只读取 exact V11 root、旧 route manifest 和旧 profile 文件，列出按
`connectionId`、`maximumConcurrency`、`dispatchTimeoutMilliseconds` 去重并稳定排序的 maintenance candidate。若只有一个
candidate，机械选择；若有多个，不猜测第一项，必须明确提供其 index。`--apply` 才会 create-new V11 backup、原子写入 V12 root，
并 strict reopen/validate；旧 profile path 原样成为 `historicalAgentControlProfileFiles`。这不是 live 实例迁移授权：先停服、确认
writer 已退出、在状态目录外备份，并另行决定任何 SessionJournal/Store/Control 的真实升级。
