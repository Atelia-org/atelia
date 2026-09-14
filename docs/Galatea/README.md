# Galatea 文档索引

日常使用从 [Galatea.Server 运行指南](../../prototypes/Galatea/README.md)开始：启动、登录、交互、启用后台 Agent、查看状态和排障都在那里。

## 按任务阅读

| 你要做什么 | 阅读入口 | 内容归属 |
|:--|:--|:--|
| 运行服务、观察或人工交互 | [运行指南](../../prototypes/Galatea/README.md) | 常用操作与故障入口 |
| 准备或修改配置 | [配置参考](configuration.md) | 必需文件、首次 scaffold、连接与状态目录 |
| 调用 HTTP、消费 SSE、理解状态字段 | [Server API](server-api.md) | 当前接口、请求示例、完整协议与限额 |
| 修改自动轮次、邮件、记忆或恢复流程 | [运行时机制](runtime.md) | 跨组件职责、持久化边界与专题链接 |
| 验证真实 Codex transport | [代行验证](codex-delegation-verification.md) | 可重复 canary；历史结果单独标注日期 |
| 对现有实例做真实 E2E、冷重开与清理 | [E2E 操作指南](e2e-testing.md) | 可复用流程、浏览器观察、测试筛选与实测经验 |
| 开发期演练升级、回退、进程 crash、记忆通知与摘要恢复 | [Scenario lab](scenario-lab.md) | 合成隔离实例、持久状态组合验收与显式 live canary |
| 处理已证实完成但无法自动结算的 Codex turn | [离线恢复 runbook](codex-delegation-operator-recovery.md) | exact evidence、dry-run 与显式 apply |
| 委派自动恢复、失败回信与失效线程重建 | [自动恢复重构方案](codex-delegation-recovery-refactor-plan.md) | 已实施与本地验证；含决策、测试和部署边界 |

## 深入到具体机制

| 主题 | 入口 |
|:--|:--|
| root config 的字段合同 | [V9 current contract](../SessionJournal/current/contracts/galatea-root-config-v9.md)，沿其引用读取继承规则 |
| prompt 的代码与 operator 分工 | [prompt 资源说明](prompt/README.md) |
| TextExtractor 与 Observation 通讯 | [Observation Bridge](text-extractor-observation-bridge.md) |
| Character Note、Default MemoPod | [忠实代写](character-note-transcription.md)、[保存合同](character-note-default-memopod-v1.md)、[自动记忆工作单](automatic-memory-work-order.md) |
| durable Codex delegation | [状态机设计](codex-delegation-durability-design.md)、[V3 resilience 实施记录](codex-delegation-local-resilience-work-order.md) |
| per-user 本地个人目录 | [User home 设计与实施](user-home-design.md)：`/galatea-homes/`、原 thread 延续、格式升级与实际验证 |
| 服务端自动轮次 | [headless pilot 工作单与验收](headless-agent-pilot-work-order.md) |
| SessionJournal / RecapGrid authority | [当前架构与代码地图](../SessionJournal/current/architecture-and-code-map.md) |
| 简化 Galatea / RecapGrid 身份与恢复校验 | [设计与实施记录](identity-simplification-design.md)：前三切片及 [Store 简化](recap-store-simplification-plan.md)已实现；[Timeline 单一行身份](timeline-row-identity-simplification-plan.md)记录代码验证及已完成的 Dev 数据升级、真实 Recap 重建 |
| RecapGrid operator CLI | [构建与即时诊断](../../prototypes/SessionJournal.Cli/README.md#构建与即时诊断)：现有 V3 catalog、人工 route、订阅环境与 stderr 进度；[本轮设计与实施记录](recap-operations-usability-plan.md) |

## 源码与验证入口

- [Program.cs](../../prototypes/Galatea/Program.cs)：启动、认证和 HTTP 路由。
- [GalateaServices.cs](../../prototypes/Galatea/GalateaServices.cs)：会话、配置加载/生成、主线执行和 HTML。
- [AutomaticTurnCoordinator](../../prototypes/Galatea/GalateaAutomaticTurnCoordinator.cs)、[HostedService](../../prototypes/Galatea/GalateaServerAgentHostedService.cs)：自动 admission 与后台驱动。
- [galatea.js](../../prototypes/Galatea/wwwroot/assets/galatea.js)：网页交互、轮询和协议校验。
- [Galatea.Server.Tests](../../tests/Galatea.Server.Tests)：服务端与浏览器协议、状态机和 provider-free 验证；live 测试显式 opt-in。

## 如何维护这些文档

1. 运行指南只增加影响实际操作的变化。字段全集、字节限额、状态机和验证流水进入对应专题，不在入口重复定义。
2. 一个细节有一个明确的维护位置；其他文档用链接和短摘要。API 字段/状态/grammar 改动同步更新 Server API；配置改动同步更新配置参考和其所属合同。
3. 工作单、设计讨论与旧验收说明各自阶段的意图和证据，不能仅凭“已完成”当成当前部署状态。旧 browser-sponsored 方案已由服务端 [headless pilot](headless-agent-pilot-work-order.md)接替；[2026-08 delegation 重构记录](codex-delegation-refactor-status.md)中的版本与测试计数也仅属于当时阶段。
4. 移动章节时同时更新引用它的文档及 fragment。尤其 [SessionJournal R2 合同](../SessionJournal/current/contracts/session-journal-contract-r2.md)引用了 Server API 的 SSE ledger。
5. 当前入口与参考文件已列入现有[文档检查范围](../SessionJournal/session-journal-doc-check-scope.txt)。新增参考页时把它加入该范围，并在 Git 中跟踪后运行：

```bash
python3 scripts/check_session_journal_docs.py
git diff --check
```

检查器验证路径与文件链接，不代替对接口、示例命令或 Markdown fragment 的核对。不要为更新文档启动 live 服务或把本地密码、认证内容、状态文件和 provider 输出写进 tracked 文档。
