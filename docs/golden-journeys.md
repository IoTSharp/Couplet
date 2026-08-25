# Golden Journeys

Golden journey 是阶段验收输入，不是演示脚本。每条 journey 都绑定 corpus manifest、Git revision、支持语言等级、预期 evidence、资源预算和 SLO；任一结果缺少 revision/source span、发生静默截断或走未声明 fallback 即失败。

## GJ-01：首次打开仓库

**任务**：用户以显式 workspace path/id 启动 Couplet MCP，initialize handshake 返回 `workspace_id`，随后 Codex 或 Claude Code 查询工作区状态和项目入口。

**必须结果**：

- 识别 repo/worktree/branch/commit/dirty state、ignore 和语言分布。
- 首次索引可取消、可恢复，状态显示总数/完成数/失败数和 active index revision。
- `workspace_status` 不会因索引未完成而虚报 current；完成后入口文件/项目/构建目标带 source evidence。

**失败条件**：固定语料大小上限、索引中断后从零开始、生成数据库污染代码仓库、未完成 generation 可查询。

## GJ-02：定位正确修改点

**任务**：Agent 收到一个功能/缺陷描述，需要找到公共入口、实际实现、约束和最近测试。

**必须结果**：

- exact/fulltext（C1）及 hybrid（C3）返回稳定、去重、可续页的候选。
- `symbol_get` 区分同名符号并返回 qualified identity、signature、container 和定义 span。
- `context_pack` 在预算内优先提供实现与约束，不用无关大文件填满上下文。

**失败条件**：仅凭文件名猜测、同名符号静默选错、引用无法回到 revision/span、结果截断不可见。

## GJ-03：追踪跨文件/跨语言调用与依赖

**任务**：从 API/命令入口追踪到存储写入或外部协议边界。

**必须结果**：

- C2 使用原生图返回方向、relation kind、逐跳 evidence/confidence 和实际 expanded/frontier 计数。
- unsupported 动态调用明确显示 unknown/inferred，不补造 exact edge。
- max depth/frontier/deadline 生效，cycle 和 supernode 结果有界。

**失败条件**：关系边表、应用层 BFS/DFS、全图加载、没有来源的调用边、不可取消路径枚举。

## GJ-04：变更影响与测试选择

**任务**：给定 base/head 或 working-tree diff，回答哪些公共合同、调用者、构建目标和测试可能受影响。

**必须结果**：

- diff hunk 映射到旧/新 revision 的文件与符号；rename/delete 有确定语义。
- 输出直接影响、传递影响、候选测试和每一条传播原因。
- 完整结论与 best-effort 结论明确区分；预算、partial parser 或 stale index 不得被隐藏。

**失败条件**：golden corpus 漏掉必需测试、把全仓测试列表当影响分析、跨 revision 混合边。

## GJ-05：大仓库与超长语料

**任务**：索引和查询远超 5,000 词、包含 monorepo、高扇出公共符号和至少 10m 关系边的大型 corpus。

**必须结果**：

- 入口不设置固定词项/符号/边数上限；容量不足时返回资源诊断，不伪装成功。
- 查询通过索引/邻接和分页执行，内存随 page/candidate/frontier/response budget 有界。
- warm/cold、P50/P95/P99、working set、分配、I/O 和 actual access path 进入报告。

**失败条件**：达到固定数量后拒绝整个仓库、exact/adjacency 随全库线性扫描、先全量物化再按 token 裁剪。

## GJ-06：持续编辑与 Git 切换

**任务**：用户连续保存、rename/delete 文件，切换 branch/worktree，并在索引过程中继续查询。

**必须结果**：

- debounce/coalesce 不丢事件；每个已发布 revision 内部一致。
- 查询明确绑定旧 current revision 或完成后的新 revision，不混合两者。
- 旧 Document/FullText/Vector/Graph 派生状态最终清理，branch 回切可复用正确 content hash。

**失败条件**：旧命中、孤儿边、重复符号、向量未失效、状态显示 current 但仍有未披露变更。

## GJ-07：崩溃、损坏与升级

**任务**：在 parse/write/publish/cleanup 各阶段 kill 进程，随后 reopen；再测试旧 schema/parser/model 升级和备份恢复。

**必须结果**：

- reopen 只暴露完整旧或完整新 generation，校验不通过时 fail closed。
- repair/rebuild 原因、范围和进度可见；恢复后 golden query 零 mismatch/orphan/index drift。
- 备份包含 manifest/checksum/version，restore 后 revision 和查询结果一致。

**失败条件**：吞掉恢复错误、自动返回可疑结果、依靠删除整个数据库掩盖可恢复性问题。

## GJ-08：Codex 与 Claude Code 等价接入

**任务**：两个客户端在相同 workspace/revision、工具 schema、预算和任务集上执行定位、修改、影响分析和测试选择。

**必须结果**：

- 两端看到相同能力、错误、证据和分页语义；安装适配不改变查询含义。
- paired eval 分客户端报告成功率、time-to-validated-patch、输入 token、工具调用次数和引用正确性。
- Couplet 不可用时客户端得到明确健康/恢复指引，不无限重试或静默退化。

**失败条件**：只对一个客户端有效、依赖未版本化 prompt 技巧、聚合指标掩盖某一客户端退化。

## Fixture 规则

- C1 fixture 至少覆盖 C# 与 TypeScript/JavaScript 两个 adapter family；TypeScript/JavaScript 按一个 adapter family 计。C3 fixture 再加入 Python、Java、Go 中至少一个达到声明 Semantic Tier 的独立 adapter family。
- 包含同名符号、partial/overload/generic、动态调用、generated code、symlink、binary、大文件、Unicode path、rename/delete 和 merge/conflict 边界。
- corpus 可由固定开源 revision 或确定性生成器构成；manifest 记录许可证、commit/hash、生成参数和期望规模。
- golden answer 变更必须单独审查，不能在修复实现的同一机械更新中无解释地接受新结果。

C0 已把上述规模、语言 family、边界场景和首批 golden identity 冻结在 `fixtures/c0`，并由确定性生成器和 evidence runner 校验。生成的 100k/1m/10m LOC 语料不提交仓库；C1 起在语言 adapter 实现后补齐真实 symbol/edge/span golden 结果。
