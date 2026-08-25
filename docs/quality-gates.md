# 质量与性能门禁

## 1. 门禁规则

每个阶段有两个独立结果：

1. **Correctness / Recovery**：语义、revision、一致性、崩溃、恢复、取消和错误合同。
2. **Performance / Capacity**：实际访问路径、复杂度、P50/P95/P99、吞吐、内存、分配、I/O 和容量。

任一结果不是 PASS，阶段就不是完成。性能失败不能用增加默认超时、内存、frontier 或返回上限处理；通用缺口必须回收到 SonnetDB，产品特定缺口由 Couplet 优先修复。

## 2. 固定语料规模

这些是验收档，不是产品最大值。Couplet 不设置固定索引词项、文件、符号或边数上限。

| 档位 | 代码规模 | 符号 | 图关系 | 用途 |
|---|---:|---:|---:|---|
| Small | 100k LOC | >= 10k | >= 100k | PR quick、合同与正确性 |
| Medium | 1m LOC | >= 100k | >= 1m | 日常工作站、阶段合并门禁 |
| Large | 10m LOC | >= 1m | >= 10m | 固定硬件容量与生产发布 |

每档包含当前阶段已声明支持的全部 adapter family、多个项目/包、测试、generated/ignored 文件、高扇出 symbol、深依赖链、cycle、rename/delete 和 dirty worktree。C1 至少为 C# 与 TypeScript/JavaScript 两个 family（TypeScript/JavaScript 合计一个 family）；C3 起至少三个独立 family，并从 Python、Java、Go 中启用至少一个已通过 Semantic Tier gate 的 family。报告记录 corpus manifest/hash，不能用不同语料横向比较。

## 3. 参考硬件与运行口径

首次 C0 evidence PR 必须归档准确 CPU 型号、物理/逻辑核、32 GiB RAM、NVMe 型号/文件系统、OS、.NET SDK/runtime、power profile 和后台负载。参考最低配置为 8 physical / 16 logical cores、32 GiB RAM、单机 NVMe；离线本地 embedding 与在线 provider 延迟分开统计。

- 普通 Release 必须报告；SonnetDB Core 和 CPL-007 能力矩阵中声明 AOT 的 Couplet executable/worker 另报 Native AOT。不能 AOT 的成熟 parser worker 必须隔离、如实标注并通过 trim/安全门禁，不以 Debug 结果设门禁。
- C0 analyzer/publish、合同 runner 与逐进程限制记录在 [CPL-007 基础与发布边界](cpl-007-foundation.md)和 [C0 Evidence Runner](c0-evidence.md)；C0 evidence 不能替代 C1-C4 的真实语料、恢复或容量结果。
- cold/warm 分开，预热、样本数、P50/P95/P99 与失败样本全部保留。
- 在线 provider 网络时间不计入本地查询 SLO，但端到端 Agent eval 计入真实墙钟和成本。
- 目标在看过候选发布结果后不得调低。确需变更必须提前用 ADR 说明语料/硬件/用户价值证据，并保留旧目标对比。

## 4. 预冻结 v1 SLO

以下是路线目标，不是当前实测成绩。C0 runner 必须先证明测量可信；C1-C4 按对应能力启用门禁。

| 操作 | Medium P95 | Large P95 | 额外条件 |
|---|---:|---:|---|
| 冷首次索引 | <= 5 min | <= 45 min | 不含在线 provider；失败文件逐项报告 |
| 100 个已存在文件的增量发布 | <= 3 s | <= 10 s | 从 filesystem event 到新 revision 可查询 |
| `workspace_status` | <= 50 ms | <= 100 ms | 不触发扫描或重建 |
| exact `symbol_get` | <= 50 ms | <= 100 ms | examined 随命中数增长，不随全库增长 |
| FullText top 20 | <= 200 ms | <= 500 ms | actual index path，无全库 scan |
| 原生图 1-3 hop、最多 200 项 | <= 300 ms | <= 1 s | depth/frontier 有界，计划/计数可见 |
| `impact_analyze`、最多 500 项 | <= 750 ms | <= 2 s | 包含测试选择和逐条传播原因 |
| 16k-token `context_pack` | <= 1.5 s | <= 5 s | 不含在线 embedding/LLM；无全量候选 merge |

资源目标：Medium 稳态 RSS <= 4 GiB，Large 稳态 RSS <= 12 GiB；单查询增量 working set 分别 <= 512 MiB / 2 GiB。超过时必须按组件和数据结构解释，不能通过进程重启隐藏。C0 evidence 可在首次实现前收紧目标；放宽遵守 ADR 规则。

## 5. Correctness / Recovery PASS

- 已声明 `exact` 的 golden definitions/relations/source spans 零 mismatch；启发式关系逐项标记 confidence/evidence。
- 所有结果绑定 workspace/source/index revision；current/stale/dirty/indexing 状态与真实工作区一致。
- rename/delete/branch/worktree/parser upgrade/model upgrade 后无孤儿 Document、FullText、Vector 或 Graph 状态。
- kill-before-publish 只见完整旧 generation，kill-after-publish 只见完整新 generation；replay/checkpoint/backup/restore/repair 全 PASS。
- MCP schema snapshot、分页、cursor revision、预算、取消、deadline 和稳定错误码全 PASS。
- 受支持 journey 的 evidence 引用 100% 可解析到相同 revision 的有效 path/span。
- 安全测试证明 ignore/deny、path traversal、symlink escape、日志脱敏和默认离线边界有效。

## 6. Performance / Capacity PASS

- Medium/Large 每个目标操作达到冻结 SLO；报告包含 commit、corpus、硬件、P50/P95/P99、吞吐、RSS、分配、I/O 和恢复时间。
- exact、FullText、Vector 和原生 adjacency 计划与真实计数一致；不允许计划显示 seek/adjacency 而运行时 scan。
- 候选、examined、returned、expanded edges、frontier peak、fallback reason 和 budget consumption 可观察且标签有界。
- 分页不从头重扫；阻塞算子内存随 page/candidate/frontier/budget 增长，而不是随全库规模增长。
- mixed read/index、supernode、深链、宽 frontier、取消和冷/热 reopen 均有结果；超时/失败计入分母。
- 发现通用 Core 缺口时，gap 带最小复现和 owner，相关 Couplet 阶段的退出/发布 gate 保持失败，直到 Core 回归和本仓复测同时 PASS；允许继续对已就绪 public API 联调取证。

## 7. Agent 效果门禁

C3/C4 对 Codex 与 Claude Code 分别执行 paired eval；使用相同客户端版本、模型、提示、任务、工作区 revision 和时间窗口，对比“无 Couplet”与“启用 Couplet”。至少 30 个有 golden patch/test 的任务，覆盖定位、跨文件修改、影响分析、测试选择和大仓上下文。

评测 manifest 必须固定模型/provider/version、temperature/seed（provider 支持时）、工具 schema、系统提示、预算和环境。每个非确定性条件每任务至少运行 5 次；确定性且 seed 可复现时至少 3 次。baseline/enabled 的先后顺序按任务随机或交替，超时、工具错误、无 patch 和环境失败使用预注册规则计入结果，不得事后删除。成功率使用 paired bootstrap 或等价预注册检验；高 baseline 场景的非劣 margin 固定为 -2 个百分点，95% 置信区间下界不得越过该 margin。

每个客户端必须同时满足：

- 任务成功率至少提高 10 个百分点；若 baseline 已 >= 90%，则成功率不下降且 95% 置信区间满足非劣。
- median time-to-validated-patch 至少降低 20%。
- median 注入代码上下文 token 至少降低 25%，同时 evidence citation 正确率为 100%。
- golden 必需测试 recall 为 100%；不能通过永远选择全量测试满足该指标，测试选择 precision 目标 >= 80%。
- stale/partial/budget-exhausted 情况下不得生成“完整影响已确认”之类错误结论。

两个客户端分别 PASS 后才能汇总；不允许用一端的收益掩盖另一端回归。

## 8. C4 长稳与发布证据

- 7 天 mixed workload，覆盖持续编辑、查询、branch/worktree 切换、增量积压、重启、backup 和 restore。
- 随机 kill/reopen 与故障注入后 golden query 零 mismatch、零 orphan/index drift。
- 数据库增长、stale generation 清理、embedding cache 和日志磁盘使用有界且可解释。
- SonnetDB Core Native AOT 和 CPL-007 矩阵中声明 AOT 的 Couplet executable/worker 均为 0 个未处置 IL/AOT warning；其余发布单元通过 trim/依赖矩阵，安装、升级、回滚和卸载在支持平台实机通过。
- 发布包附 capability matrix、known limitations、开放 gap、容量报告和 Agent eval；存在 Production blocker 时不得发布 1.0/GA。
