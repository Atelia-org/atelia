# 固定旧 Control / Journal 续行样本

这些是 `6a544481` 基线生产代码生成的合成测试状态：Control writer v2、AgentControl output v1、Prepared v8。捕获发生在本切片 production 修改前，focused capture 测试最终 3/3 通过，三个样本均断言 Control generation=1 且随后检查确有一条 terminal receipt。没有私人会话、凭据、真实模型调用。connection 使用 `https://example.invalid`。

生成使用 `ProgramRecapGridCommandTests.ToolContinuationBindsFrozenProfileAndReplaysReceiptAfterRewind` 的 init/admission 配置和 `ControlToolCallClient`，向初始 Journal 发送固定 `provision from tool`，模型替身返回 `provision-built-in(mystery-investigation-v4)`。AgentControl 绑定同一个正在执行的 Journal engine 的 ReadView，保持 owner 生命周期；只使用现有 hooks。

- `after-control-commit.zip`：`AfterToolExecutionBeforeResultCommitted` 实际中断并关闭所有 handles；Control 已有 terminal receipt，Journal head 为 ToolExecutionStarted，尚无 ToolResultObserved。
- `after-tool-result.zip`：`AfterToolResultCommitted` 实际中断并关闭 handles；Journal head 为 ToolResultObserved，下一 Prepared 尚未产生。`expected-tool-result.json` 固定当时事件 payload 原文。
- `prepared-with-old-tool-result.zip`：先按上个窗口暂停，再由基线 CLI `run-online-turn` 与确定性假 Client 完成续行；把测试 ref 定点至已经提交的下一 Prepared。**该样本只证明已有冻结 Prepared 的重构/恢复，不作为跨提交窗口 crash 的证据。** `expected-request.json` 和 `expected-exact-inputs.json` 是基线重构输出，tool result payload 同样固定。

三份合成目录总计约 36 KB 压缩字节。保留原 Journal、Control、Timeline、Store、Cadence 和最小配置关系；保留零字节 lock 文件（只读入口要求它们已存在），未重编码旧持久数据。SQLite/RBF 内容不依赖新 writer 生成，测试只解压到自己的临时目录。不要用新 writer 换版本号或把当前 DTO 加旧字段来刷新这些样本。

ZIP 解压不会恢复目录权限；测试提取 helper 在 Unix 上将目录/文件恢复为 0700/0600，满足 Cadence 的原有打开合同。该步骤只改测试副本权限，不改文件正文。固定 Prepared 的 `ExactContextInputs` 为 `[]`；这里验证已有工具结果保真，不冒充非空 Recap 恢复覆盖。

| 文件 | 压缩字节 | SHA-256 |
|---|---:|---|
| `after-control-commit.zip` | 11,144 | `75733c8fcb285d3ca9c0e535cf6ef037e4f4c2f70cae42a7de3cd5316b794515` |
| `after-tool-result.zip` | 12,107 | `5f42c26489842eba331395991e2487c7f1b712c654dd288e45170044fa73192b` |
| `prepared-with-old-tool-result.zip` | 13,803 | `7e14511299a3bd204ffc4268bdf1f034e00a0bf8a3a1f392af5d20c70631449d` |
