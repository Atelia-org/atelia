# Codex 空启动投影与 live 观察

> **当前格式边界（2026-09-16）**：本文保留 sparse Start／live observation 的故障分析与信任边界；同 generation 的相关 RPC／通知和 cold 历史核对两条证据链继续适用。当前 delegation SQLite V5、wire V6 的完整任务承诺统一为严格 UTF-8 摘要／长度；旧全文或摘要算法的阶段描述不作为新写入合同。详见[结构化输入方案](structured-input-rendering-design.md)与[当前运行时](runtime.md)，最新真实调用与迁移证据见[迁移验收](player-character-migration-validation.md)。

最小模型：同一 app-server generation 内，原 `turn/start` 请求保存 dispatch/task，关联响应补齐 turn ID；item 通知补充正文，terminal 通知决定完成。进程结束即清空 live 观察，冷恢复继续匹配持久历史。

## 2026-09-14 故障证据

Galatea debug 日志显示，17:28:29 收到 Accepted，随后两次 `ACCEPTED_TURN_NOT_VISIBLE`，再持续 `RUNNING_NOT_CONFIRMED`，到 17:29:44 累计 7/8 次。17:30:25 的 sidecar 退出发生在 host shutdown 期间，晚于故障。只读 SQLite 检查确认邮件仍为 Accepted、连续失败 7 次；对应 rollout 有 task_started 和多次工具调用，未找到 task_complete/final_answer。日志没有保存该次原始启动 RPC，因此不能声称捕获了现场 wire。

[官方 app-server 文档](https://learn.chatgpt.com/docs/app-server)说明 `turn/start` 初始响应及 `turn/started` 可以携带空 items；本机 Codex 源码也主动清空这些投影。旧观察器在 sparse response 中直接返回 Accepted，却不建立 observation；随后删除 pending expectation。迟到 user item 只更新已有 observation，因而也不能修复缺失的关联。

独立原生实验使用项目固定的 `0.154.0-alpha.3`、隔离 HOME/CODEX_HOME 和 localhost 合成 Responses SSE，实际确认：启动响应和 turn/started 都是空 items/notLoaded；userMessage 从独立 item/completed 送达；turn/completed 是 summary，只有 agentMessage。该结果不依赖本机源码与安装包版本相同，也不假设跨通道的事件到达顺序。

无模型复现使用生产 CodexBackend、JSON RPC client 与 fake app-server：一次 sparse start 后，仍在运行的合成任务被检查为 `running/source=persistent`，并访问 turns/items 历史分页。Galatea 正确地不把历史 inProgress 当成活性证明，于是该上游缺陷被计入有限恢复预算。

旧测试默认把完整 user item 填进启动响应，掩盖了缺陷；单独的 sparse 测试只验证快速任务能从历史取到 final。README 又把“不建立 live”写成了契约，这只是同一实现假设的重复记录。

## 需求与裁决

当前用户要求调查、组织独立辩证审查，并实施满意的根因修复。真实消费者是 Galatea Driver、V5 sidecar 与普通 MCP backend；已有 SQLite V3 和 Codex 历史需要继续读取。当前故障模型包含异步通知、投影延迟、进程重启和已可能发送的任务。

三位独立评审从需求、最小架构和语义保护出发，交换最强反例后收敛：

| 裁决 | 机制 | 证据与边界 |
|---|---|---|
| simplify | 关联响应建立 exact live 身份 | RPC 已保存 request→response 因果关系，无须等 user echo 才开始跟踪；不能把任意 turn/started 猜成当前请求 |
| merge | 每个 thread 只保留一个 observation Map | 原 observations 与 currentTurnByThread 重复维护相同关系；lookup 仍核对 turn ID |
| delete | userFingerprint | exactUser 只含 id/dispatch/task，已有 itemId、dispatchId、taskDigest 检查覆盖相同信息 |
| keep | generation、terminal barrier、final 一致性 | 早到 terminal 不能被迟到 inProgress 响应复活；summary 缺 final 不能伪装成功；user/final 同 ID 在两种到达顺序都必须拒绝 |
| keep | 冷恢复与有限预算 | 死进程可留下永久 inProgress，不能凭历史状态清零；可能已执行的任务不能自动重发 |
| defer | 通用事件缓存、扩大恢复次数或重写状态机 | 本次没有新增消费者或失败场景要求这些机制 |

无需新增 wire 字段、SQLite schema、持久账本或迁移。实施范围是 live 观察器、符合真实形状的 fixture 和回归验证。测试结果见[验证记录](codex-delegation-verification.md)。

## 现场边界

修复不会为已退出进程补造 live 证据。当前旧邮件只能按已有 cold reconciliation 检查原 turn；没有终态证据时，有限恢复会诚实结算为 RESULT_UNCONFIRMED。它不证明旧任务完成或被撤销。本次代码修复不重发实际任务，也不改写真实用户数据库。
