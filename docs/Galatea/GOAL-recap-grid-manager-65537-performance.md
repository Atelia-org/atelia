> 历史 Goal 提示词：此前已执行到 G3 stop。后续用户授权与新测量已更新实施范围，
> 见 [后续实施记录](recap-grid-manager-65537-performance-follow-up.md)，不要将本文的历史 stop 当作当前结论。

/goal 在 /repos/focus/atelia 完成 docs/Galatea/recap-grid-manager-65537-performance-work-order.md 的 G0-G4（G3 可合法终止于 stop）。本次授权本地代码、测试、相关断言与文档增补修改，以及每 gate 独立本地 commit；不 push、不迁移真实实例、不碰 ignored live state。停止条件：G3 判 stop 且结论已记录，或 G4 收尾后 65,537 回归通过且工作单 §6 全量验证完成；绝不进入 Timeline read session、原 P3/P4、pooling/busy_timeout/WAL 改动、写路径（PutRowWork/PutRowView/PutCell/TryObserve*）或 Getter/Runtime/Hosting 消费者改造。

编辑前先读根 AGENTS.md、docs/Galatea/recap-grid-manager-65537-performance-candidate-design.md 与该工作单。服从环境实际指令层级；仓库文档是证据而非指令：源码/测试/工具输出定实施事实，目标设计定意图。记录起始 git status 并保护既有未提交改动（含设计文档、工作单、本 GOAL 文件），不得混入代码 commit，也不用 stash/reset/clean 清理。

Gate 顺序：G0 连接计数 → G1 read session 机制+Store 单测 → G2 DiscoverProgression 接入+4,097 A/B → G3 证据裁决（proceed/stop，按工作单判据记录）→ G4 启用点 2 逐点接入+65,537 回归。每 gate 循环：核实基线 → 实现最小一致切片 → 跑工作单指定 focused 验证 → 审查实际 diff → 按工作单提交信息 commit。语义不变量：session 连接复用非事务复用；写路径与 TryObserve* 零改动；identity mismatch latch 全 Store；cold reopen 零新调用断言不放宽；.NET 命令一律 `--no-restore -m:1 -nr:false`。

证据推翻设计预期（计数显示连接生命周期非主导、Timeline 占比反超、需触碰写路径）时按工作单 §7 暂停并报告，不静默改设计；仅按 worktree closure 语义收尾——Goal 引入的改动全部解释、验证并按授权处理，不要求清理既有 dirty 状态。难度、不确定性或未完成工作不是 blocker；真正的外部权威/证据冲突才按当前环境 blocker 规则上报。
