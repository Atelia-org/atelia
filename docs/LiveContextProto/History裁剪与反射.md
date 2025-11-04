历史裁剪与缓存：引入 HistoryLimitOptions，支持“最大条数/总字节数”；同时缓存最近构建好的 IReadOnlyList<IHistoryMessage>，只在有新 Entry 时增量更新。

引入对历史的反射编辑能力，用以支持RecapMaintainer和Daemon/Analyzer SubAgents
