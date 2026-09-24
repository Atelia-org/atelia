# 角色状态驱动的连接选择

状态：已实施并完成本地验证（2026-09-25）；开发实例 binding 已配置，尚未重启服务。本文记录行为、实施边界和验收入口。

## 目标与取舍

每个 Character 的 `connectionOptions` 定义 `connectionId`、`name`、`trigger`。trigger 是回合结束时的状态条件，例如穿着裙装、穿着裤装、戴着眼镜或未佩戴眼镜。辅助模型从成功完成的终结 Action 提取唯一匹配，宿主设置进程内连接选择。识别失败后，后续回合仍可凭持续的状态摘要校正；不依赖一次性的换装动作或 toggle 事件。

新接纳回合的连接优先级保持：`diagnosticConnectionId ?? runtimeConnectionOverrideId ?? defaultConnectionId`。选择在接纳时固定；改变 override 不改变已接受回合或 Prepared 的恢复身份。诊断调用产生的正常剧情参与状态识别，但诊断 ID 不直接写入 override。

## 状态与识别

- 主线协议要求 `[状态摘要]` 持续记录与 trigger 有关的已成立事实，包括未改变的状态；未知时不得补造。
- 使用专用 `CharacterConnectionStateExtractor` 复用 `TextExtractor` / `ArtifactToolWrapper`。输入是本角色身份、同一份选项快照和终结 Action 可见文本；工具产物携带 connectionId 和有界原文证据。
- 只有当前角色已经成立的最终状态可匹配；计划、引用、回忆、其他角色、未成功的动作均不构成匹配。没提眼镜不等于未佩戴。摘要与叙事明显冲突或多条件同时成立时放弃切换。
- 提取器提供两个互斥工具：`emit_character_connection_state` 提交 connectionId/evidence，`emit_character_connection_state_unknown` 用空对象明确报告无匹配、歧义或冲突。整个结果最多一个产物，零工具调用也保持原值。重复匹配同一 ID 不产生新切换记录；多个产物、越权 ID、空 trigger、无效证据或工具输出错误均使本次识别失效，不能部分应用。
- `name` 用于展示语义，`trigger` 用于匹配，`connectionId` 是唯一精确标识。空 trigger 不参与自动识别，仍可用于诊断选择。
- runtime 连接不是故事状态的证据。不能因默认/诊断连接而反推衣着、物品或人物意图。

## 执行与生命周期

成功完成回合且终结 Action 已落盘后，在持有 Character `TurnLock` 时执行识别，并在下一回合接纳前应用结果。识别独立于 mail/note 的 durable settlement，不引入 durable 提取计划。覆盖人工消息、人工邮件、内部角色信、DelegateReply、心跳和恢复后成功完成的回合；失败或停止且没有成功终结 Action 的回合不识别。

识别使用整体时限（首版 30 秒，覆盖辅助客户端重试），失败/超时保留原值并记录 content-free 诊断，不把已完成剧情变成失败。取消遵守宿主生命周期；不能将仍在运行的提取任务遗弃在锁外。工具回调只收集产物，验证全部结果后由宿主原子替换状态。

override 和最近切换记录只存在于进程内；冷启动初始为空，新回合在尚未识别出状态时使用 default，不扫描历史或重放动作。已有回合恢复完成后的识别也可建立选择。恢复匹配可能需要多回合，不能承诺只偏差一回合。撤销最新回合成功后清除本角色 override 与最近切换记录；后续剧情重新建立选择。

## Observation 连接快照

每个新接纳的主线输入携带冻结的 `connectionState`，包括 `runtimeOverrideConnectionId`、`effectiveConnectionId`、`turnConnectionId` 及可空 `lastChange`。前三者分别是内存选择、常规有效选择和该回合实际绑定连接。快照覆盖全部触发类型，包括 inbound-mail。

实际常规有效连接变化时形成进程内事件记录：来源 Action 地址、旧/新 connectionId、选项名、匹配证据。作为 `lastChange` 随快照重复呈现，明确表示最近一次历史变化，不是新指令，不承诺 exactly-once 通知。null 到 default 的显式赋值不算有效连接变化。

无需 outbox、消费确认或额外数据库。快照随已有 Observation 正常落盘；它是历史输入证据，不是恢复 override 的权威。已有输入的恢复必须重用其快照，不能读当前内存改写。新字段使用新 Observation schema，旧输入继续按原 schema 投影；内部信证明、回执和输入限额等消费者须同步核对。

`turnConnectionId` 精确描述新回合接纳时绑定的连接。已有恢复 API 对兼容的 NewRequestRequired 仍可能允许诊断连接；这种恢复不改写历史快照，快照不能用来证明每次恢复 transport 的连接身份。Frozen request 的目标约束保持原样。

## 提示词与配置

新增 binding `galatea.character-connection-state-extractor`，字符串指向独立辅助连接，null 禁用。绑定启用且角色存在非空 trigger 时才启用该角色识别并追加机制说明。

新增 embedded appendix `prompt/trpg-character-connection-state-appendix-zh-cn.md`，沿用邮件/note 的固定协议 source。角色 `connectionOptions` 保存为 typed Setup 的 bindings 数据，经现有 md-json 投影给主线模型；辅助识别使用相同选项。禁止维护第二份人工选项表或在恢复时从当前配置替换历史 Setup。扩展 SystemInstruction schema，保留旧 Setup 精确读取；动态连接快照放在 Observation，不反复改写 system setup。

根 config 的 connectionOptions 形状保持 V14；Completion catalog V3 增加一个 Galatea 必需的可空 binding。首次 scaffold 默认禁用；本地现有实例按授权加入辅助连接 binding，并保留备份。修改本地配置不表示服务已重启或真实会话完成升级。

## 验收

1. 工具调用结构、唯一性、精确角色 allowlist、证据校验；正文不当作产物。
2. 重复匹配幂等；未知、冲突、超时、失败保留现状；两个角色互不影响。
3. 人工、自动、内部信和恢复完成共用完成后识别；下一新回合生效；诊断优先且不直接污染 override。
4. 快照在接纳时固定；所有触发携带；历史恢复不重算；冷启动及撤销清空进程状态。
5. 主线 prompt 的 capability gating、角色配置绑定、特殊字符和旧 Setup 投影；默认模板与配置严格校验。
6. provider-free 定向与服务端回归测试；如执行 live 提取 canary，应使用合成文本和隔离客户端，不操作现有角色会话。

## 源码入口

- [TextExtractor](../../prototypes/Galatea/TextExtractor.cs)
- [状态提取器](../../prototypes/Galatea/CharacterConnectionStateExtractor.cs)
- [进程内选择、快照与完成后识别](../../prototypes/Galatea/GalateaHostService.ConnectionState.cs)
- [GalateaHostService / 配置装配](../../prototypes/Galatea/GalateaServices.cs)
- [Completion owner](../../prototypes/Galatea/GalateaCompletionOwner.cs)
- [SystemInstruction](../../prototypes/Galatea/GalateaSystemInstructionContent.cs)
- [Observation](../../prototypes/Galatea/GalateaObservationContent.cs)
- [主线协议 source](prompt/README.md)

## 实施与验证记录（2026-09-25）

- 新增辅助 binding、默认禁用的 scaffold、角色 capability gating、SystemInstruction v2 选项快照、Observation v3 连接快照。历史 Setup v1 和 Observation v1/v2 保留原格式读取；没有改写已有 Journal。
- 每角色以一个不可变内存值保存选择及最近切换，接纳时读一次形成回合快照。完成后在角色锁内调用提取器；撤销成功后清空状态。内部信的输入证明和 RecapGrid/CLI 共享投影同步接纳 v3。
- 新 reply lease 的预算预留最大连接快照的 JSON 转义开销；历史输入校验不因此收紧。回归中发现一个 note 测试 helper 未持锁直接追加 Journal，与后台 DerivedInfo 读取竞争，已在该测试 helper 中补锁。
- 初次真实 DeepSeek 样本出现“未提眼镜却选择眼镜状态”的误判，促成显式 unknown 工具。另一个冲突样本表明需先比较叙事和状态摘要，再匹配选项；当前提示词将冲突检查置于选择之前。没有加入模型投票、状态机、历史扫描或业务正则匹配。
- 当前 Completion declaration 对 nullable scalar 只标记可省略，不能据此接收显式 JSON null。因此使用两个互斥 artifact 工具表达结果，不修改上游包或增加兼容层。

验证结果：

| 验证 | 结果及范围 |
|:--|:--|
| 服务端完整非 live 回归 | 1324/1324 通过；最终 unknown 工具调整前运行，覆盖主线、邮件、恢复、note、RecapGrid 既有行为 |
| 最终双工具实现定向测试 | 82/82 通过，覆盖配置、提示词、工具边界、快照和真实宿主路径 |
| 最终提示词调整后提取器与 live 测试 | 11/11 通过，其中 7 个 DeepSeek `deepseek-v4-flash` 真实合成语义样本，另 4 个工具边界测试 |
| CLI 结构化历史验证 | 5/5 通过，包含旧输入与 v3 连接快照混合历史的公开 build 路径 |
| 构建与补丁格式 | 构建零警告、零错误；`git diff --check` 通过 |
| 文档检查 | 本轮文档相对链接无缺失；完整 scoped checker 仍报告 4 条既有 AgentControl 失效链接 |

Live 测试为 opt-in：`ATELIA_RUN_GALATEA_CONNECTION_STATE_LIVE=1`，依赖 `DEEPSEEK_BASE_URL` / `DEEPSEEK_API_KEY`。只发送合成文本，不打开角色会话、不保存 provider 正文。样本通过不等于任意剧情的语义识别都可靠：程序验证结构、角色选项和原文证据，证据是否真的蕴含条件仍依赖模型判断；后续回合的持续状态摘要提供校正机会。

本地 ignored `connections.json` 已加入 `galatea.character-connection-state-extractor: gpt-6-luna-codex`；保留用户填写的四个状态条件。原文件备份位于 `/mnt/wsl/fast/tmp/galatea-connection-state-ujzpakj7/connections.json`。此机器临时路径仅为本次操作记录。未重启 Galatea、未接触角色历史；真实开发会话尚未进行切换验收，DeepSeek 合成 canary 不证明配置的 Codex 辅助连接已在角色会话中运行。
