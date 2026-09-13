# 固定 Control v2 样本

由 `6a544481` 的原生产 writer 于 2026-09-14 生成，无私人数据。
创建最小 SessionJournal 与空 Timeline，使用既有 Control fixture family 执行
`legacy-v2-operation`（sequence 1，runtime digest 为 64 个 `a`），随后走真实 backup API。

`repository.zip` 是所有 handle 关闭后的完整合成目录，包含 Journal、Timeline、Control 和
`legacy-backup`。两个 JSON 文件是 backup 的独立明文副本，便于审查旧格式完整字节。
测试直接读取这些固定内容；不得用当前 writer 换版本号重造历史成功样本。
