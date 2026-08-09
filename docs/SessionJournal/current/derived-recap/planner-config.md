# DerivedRecap planner config v3

canonical path：`config/recap-planner-config.json`。schema：
`atelia.session-journal.recap-epoch-config.v3`。

文档只描述新的shared epoch：cadence、ordered profile catalog，以及raw/epoch/operation/call/roster和
aggregate byte caps。旧v2 schema与旧字段没有compat reader。

加载时序：

1. 先检查/恢复Store内frozen Building或Published damage；
2. 仅在需要规划新epoch时惰性加载active v3；
3. 新topology必须与上一完整publication roster相同，否则FullRebuildRequired；
4. first complete roster永久放不进单epoch/operation预算是ConfigurationLimit；
5. 已有durable progress但本次剩余预算不足是MoreWorkPending，且新epoch零dispatch。

`MaxMaintainerCallsPerEpoch`约束完整pending roster，不能只数成功binding的members。operation telemetry按
实际StartedCallCount；pre-dispatch budget仍按完整pending count。正常path超过raw cap只报告
FullRebuildRequired，不隐式创建rebuild spool。

Store aggregate byte/count caps是当前binary的durable hard limits；v3文档必须精确声明这组值，
但不能在active reload中改变它们。变更这组caps需要新Store generation或显式reset决策；
`inspect`会拒绝与binary hard limits不同的文档。

CLI `recap planner-config init`写默认canonical v3，`inspect`做strict decode+host profile resolution。
