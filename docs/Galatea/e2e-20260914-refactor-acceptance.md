# Galatea 重构后 Dev 实例 E2E（2026-09-14）

代码起点 `0e7c7cdc`，工作区干净；对象为唯一 Dev 实例 `prototypes/Galatea/.atelia`。
用户授权真实 E2E 及过程中必要的修复。此前已完成 Timeline schema 3 / Recap Store schema 4
切换，本轮验证浏览器实际使用，不再次迁移或重建数据。可复用流程见 [E2E 操作指南](e2e-testing.md)。

## 结果与范围

| 验证 | 结果 |
|---|---|
| 真实 Chrome 登录 cyber / gpt | current、recent、Agent、Mailbox 正常；补验 cadence 接口及页面 |
| Anthropic `opus4-6` / `claude-opus-4-6` | 冷重启前后各一次，均 SSE `done`、回答显示、输入清空、current=Idle |
| Codex `gpt-6-astra` | 冷重启前后各一次，同样通过 |
| 冷重启后的历史 | 第一轮两位用户的实际可见输入和回答逐字保持 |
| Recap 页面 | cyber 为 exact/ready，两个 context header 正文非空；gpt 为 exact/raw-only，无摘要 header |
| 新 CLI 配置装配 | 原 V3 connections 与原 route，progress=complete；零调用预算 build=fulfilled，NewCalls/CellsCommitted/RowViewsCommitted 均 0，整仓文件未变，未创建 call-log 目录 |
| 清理与最终冷启动 | 四条探针均由页面 Undo 清理；最终页面无 console/page/API/request 错误，两位用户 Idle |
| 离线验证 | 所有活动命名分支 full/selected audit 通过，7 个活动 SQLite 库 quick_check=ok |

共 20 次真实 Completion 调用，全部成功：Opus 2、Astra 2、Luna 辅助调用 12、DeepSeek normalizer 4。
保持原连接、环境、后台 enrollment 和辅助功能，没有新增发信。没有未决 Prepared，因此本轮没有
主动制造未知模型结果或执行恢复重试；也没有跨越 cadence 阈值产生新的 Recap 行，不能将零调用复用
验收当作新的真实摘要生成验收。

## 实测发现并修复的 Undo 竞争

cyber 第二次 Undo 在页面 Idle、按钮可用时收到 `409 turn-busy`；日志为 `runningTurn=<none>`，
同刻 recent 查询也出现 `503 recent-view-busy`。原 `pop-latest` 使用 `TurnLock.Wait(0)`，而 recent、
cadence 和后台检查也持有这把锁，短时竞争被当成生成中拒绝。

修复仅改变 pop 的取得锁方式：已有 published live turn 仍直接 busy；否则最多等待 1 秒，支持请求取消。
取得锁后沿用原 exact head 校验和 CAS；等待中有其他操作改变 head，仍拒绝撤销。
不自动重发 POST，不放宽未知完成或 rewind token 边界，不关闭正常轮询。

新增两项 Host 测试覆盖短时锁释放后成功、等待期间 head 改变后拒绝；既有长持锁测试仍验证 busy 且
不移动 head。最终 Galatea 非 Live 全套 **987 通过、0 失败、0 跳过**，Node HTTP/UI 合同测试通过。
编译无错误，最终 build 为 0 warning；最初误重叠的两次 build 出现过一次 MSB3026 复制重试，之后串行
重验通过。修复后两位用户剩余的真实 Undo 均成功，原失败报告保留，不计为通过。

## 数据与结束状态

| 用户 | 初始及最终 main head | 初始及最终事件数 / phase |
|---|---|---|
| cyber | `ej1:00000bdd180003860000000100000000` | 231 / Idle |
| gpt | `ej1:00001347d40004d80000000100000000` | 253 / Idle |

最终冷重开未改变清理后的 head。Journal 追加数据、ref 历史、Timeline/Store 的正常选择与投影记录、
Note/委派 action capture 等副作用仍保留；Undo 不还原整个目录。配置、Control、Cadence 未改变，
没有新增或删除数据文件；原邮件、回信、Character Note 和 Note 回执表逐行保持。

完整 `.atelia` 前后备份均位于 `/mnt/e/bak/`，通过 `7z t`：

- `galatea-refactor-e2e-20260914T084819Z.7z`
- `galatea-refactor-e2e-20260914T084819Z-verified.7z`

私有操作报告、调用元数据、失败报告、数据库比较和审计位于
`gitignore/galatea-refactor-e2e-20260914T084819Z/`，不提交凭据或剧情正文。
服务与临时浏览器均已停止。没有 push。
