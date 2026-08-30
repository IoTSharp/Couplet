# Couplet

Couplet 是一个面向 Codex、Claude Code 等编码 Agent 的本地优先代码知识与上下文引擎。它把代码解析、增量索引、原生属性图、全文/向量混合检索和有预算的上下文组装连成一条可核验链路，并以嵌入式 `SonnetDB.Core` 作为唯一数据引擎。

> C0 基础与合同已于 2026-08-25 完成。C1 已实现工作区/Git 发现、C#/TypeScript/JavaScript partial 适配器、增量规划和 SonnetDB Document/FullText staging。默认独立构建仍固定 `SonnetDB.Core 3.1.0` 并保持查询 unavailable；显式 `UseSonnetDbSource=true` 联调最新 SonnetDB 源码时，`index-stage` 已接入 `Tsdb.Generations` 的原子发布、active lease/cursor、cutoff-aware cleanup 和提交边界故障回归，显式数据库 MCP 宿主已用 per-request active lease 接通 `workspace_status`、`code_search` exact/fulltext Preview 与 `symbol_get`。查询 cursor、真实子进程 kill 与容量门禁仍未完成。

## 产品边界

- 索引库不设置固定的文件数、词项数、符号数或边数上限；实际容量由磁盘、内存和已公布的容量报告决定。
- 单次工具调用仍受 `max_items`、`max_tokens`、`max_bytes`、deadline 和分页游标约束。任何截断都必须显式返回原因和续页游标。
- 代码关系只写入 SonnetDB 原生属性图。禁止使用关系边表、应用层 BFS/DFS、第二套图存储或隐藏全量扫描补齐缺失能力。
- Couplet 只依赖 SonnetDB，不允许 SonnetDB 反向依赖 Couplet：

```text
Codex / Claude Code / other MCP clients
                    |
             typed read-only MCP
                    |
                 Couplet
                    |
              SonnetDB.Core
```

- 一旦真实仓库、执行计划或固定硬件报告暴露通用存储、图、全文、向量或混合检索缺口，该缺口必须回收到 SonnetDB 对应里程碑优先修复，并阻塞 Couplet 的相关发布阶段。

## C0/C1 可执行面

solution 明确分离 `Couplet.Core`、`Couplet.Application`、`Couplet.Infrastructure.SonnetDb`、CLI、daemon 和 MCP Server 进程。默认 lane 固定引用官方 `SonnetDB.Core 3.1.0` package，lock file 同时冻结 package content hash 且不依赖相邻 checkout；跨仓联调通过显式 `UseSonnetDbSource=true` 直接 ProjectReference 最新 SonnetDB 源码，并把 source restore lock 隔离到各项目 `obj/`。handshake 会区分 `fixed_package` 与 `source_project`，报告实际 assembly、trim/AOT 元数据、public API 联调状态和阻塞发布的 gap。

```powershell
dotnet restore Couplet.slnx
dotnet build Couplet.slnx --configuration Release --no-restore
dotnet test tests/Couplet.Tests/Couplet.Tests.csproj --configuration Release --no-restore
dotnet restore Couplet.slnx -p:UseSonnetDbSource=true
dotnet test tests/Couplet.Tests/Couplet.Tests.csproj --configuration Release --no-restore -p:UseSonnetDbSource=true
dotnet run --project src/Couplet.Cli -- capabilities
dotnet run --project src/Couplet.Cli -- c0-evidence --repository . --commit working_tree
dotnet run --project src/Couplet.Cli -- workspace-scan --workspace .
dotnet run --project src/Couplet.Cli -- index-stage --workspace . --database artifacts/c1-smoke-db
dotnet run --project src/Couplet.Cli -p:UseSonnetDbSource=true -- index-stage --workspace . --database artifacts/c1-source-db --retired-generation-retention 1.00:00:00
dotnet run --project src/Couplet.Cli -- c1-capacity --repository . --scale medium --workspace artifacts/c1-medium/workspace --database artifacts/c1-medium/database --report artifacts/c1-medium/report.json
dotnet run --project src/Couplet.McpServer -p:UseSonnetDbSource=true -- serve --workspace . --database artifacts/c1-source-db
```

`workspace-scan` 只输出 workspace-relative path 和去凭证仓库身份。默认 package lane 的 `index-stage` 继续只写 generation 独立 staging collection，并返回 `published=false` / `CG-005`。source lane 额外把不含源码正文的 planning snapshot、Document collection 与 FullText index 交给 `Tsdb.Generations` 原子发布；无变化重跑复用 active generation。`--retired-generation-retention <c>` 接受 invariant `TimeSpan`，表示 retired generation 从发布时间起达到该时长后才具备清理资格；默认零值保持立即清理，active 或仍有 query lease 的 generation 不会删除。source 报告使用 `couplet.index_stage.v2`，分别返回 lease-deferred 与 retention-deferred revisions；冻结的默认 package `couplet.index_stage.v1` payload/schema 不变。writer fence 覆盖锁内 fresh discovery、active read、build、plan 和 publish；发布已提交后的 cleanup 失败不会改写为发布失败，而是返回 retry limitation。内部确定性故障点冻结了 SonnetDB 原子 publish 的提交边界，但不是子进程强杀证据。`c1-capacity` 仍是未发布 staging characterization，两档 Performance/Capacity gate 均为 FAIL。固定 3.1.0 package 的 win-x64 Native AOT 仍关闭不兼容 worker 并报告 CG-006；最新 source lane 已修复 worker 路径，2026-08-29 Native AOT 首次 publish/no-op smoke 已通过，但本轮 CG-007 变更尚未重新执行 Native AOT publish，7 天长稳仍未归档，JIT handshake 也不把 source 选择外推为 AOT 已验证。

source lane 的 MCP Server 在同时显式提供 `--workspace` 与 `--database` 时，用真实 workspace discovery identity 打开数据库，并在完整 stdio host 生命周期内持有 store。`workspace_status` 每次请求获取并释放一个 active generation lease，只读取同一 planning/manifest snapshot，校验资源角色/名称、schema、identity/state 和 checksum 形状；它不会扫描 Document 行、重算 checksum 或触发 rebuild。返回的 source revision 与 database bytes 是 MCP 启动时采样值，freshness/diagnostics 会明确该边界，`RebuildRequired` 仅相对 initialize 快照判断。`code_search` exact 使用 `by_stable_id` path index，fulltext 使用 generation-bound `code_search` FullText index；`symbol_get` 按 stable ID 或 qualified identity 使用 `by_stable_id` / `by_qualified_identity`，缺少 language 的歧义请求 fail closed。三个查询都在同一 active lease 内返回 source-generated typed 响应、source evidence、实际 access path 和预算诊断，不执行 Document 全扫。默认 package lane 仍 unavailable；query cursor 和 fulltext filter plan 尚未接线，CG-005 保持 verifying，CG-007 的选择性 cleanup API/接线缺口已关闭。

## 目标能力

| 能力域 | 结果 |
|---|---|
| 工作区与 Git | 识别仓库、分支、revision、ignore、重命名和 diff，持续报告索引新鲜度。 |
| 语言理解 | 通过可替换语言适配器生成文件、符号、定义、引用、调用、继承、导入和测试关系，并保留来源位置与置信度。 |
| 增量索引 | 文件变更、切分、解析、embedding 和派生索引按 revision 原子发布；崩溃后不暴露混合 revision。 |
| 原生图分析 | 提供符号关系、依赖路径、调用链、变更影响和测试选择，遍历由 SonnetDB 原生图执行。 |
| 检索与上下文 | 组合 exact、FullText、Vector 和 Graph，按 token 预算去重、排序并输出带证据的 context pack。 |
| Agent 接入 | 提供版本化、typed、首版只读的 MCP 工具，以及可诊断的 Codex / Claude Code 接入。 |
| 质量与性能 | 以 golden journeys、差分正确性、崩溃恢复、固定硬件容量和 Agent paired eval 作为发布门禁。 |

## 首版 MCP 工具

`workspace_status`、`code_search`、`symbol_get`、`symbol_relations`、`dependency_path`、`impact_analyze`、`change_context`、`context_pack`。

工具在依赖能力未就绪时返回结构化的 `capability_unavailable`，不会走语义不同的旁路。完整字段和预算合同见 [MCP v1 合同](docs/mcp-v1-contract.md)。

## 路线

- **C0 基础与合同**：可运行骨架、SonnetDB 单向依赖、typed MCP schema、评测语料和证据框架。
- **C1 增量代码索引**：工作区/Git、语言适配器、Document/FullText 索引和基础只读 MCP。
- **C2 原生图代码智能**：符号关系、路径、影响和测试选择；与 SonnetDB `#347~#351` 联调，`#352` 是联合 Preview 发布门禁。
- **C3 混合检索与 context pack**：本地 embedding、FullText/Vector/Graph 组合；与 `#353~#358` 联调，`#359` 是联合 Beta 发布门禁。
- **C4 生产与 Agent 体验**：大仓容量、恢复、安全、分发和 Codex / Claude Code 验收；与 `#360~#366` 并行收集证据，`#367` 是联合 1.0 门禁。

逐阶段交付物、依赖和退出门禁见 [ROADMAP.md](ROADMAP.md)。

## 文档

- [架构](docs/architecture.md)
- [Code Graph v1 合同](docs/code-graph-v1-contract.md)
- [MCP v1 合同](docs/mcp-v1-contract.md)
- [安全、隐私与数据生命周期](docs/security-and-data-lifecycle.md)
- [C0 合同与 Evidence Runner](docs/c0-evidence.md)
- [C1 增量索引实现与证据](docs/c1-indexing-evidence.md)
- [C1 Medium/Large 容量证据](docs/c1-capacity-evidence.md)
- [Golden journeys](docs/golden-journeys.md)
- [质量与性能门禁](docs/quality-gates.md)
- [能力缺口目录](docs/capability-gaps.md)
- [SonnetDB 能力矩阵](docs/sonnetdb-capability-matrix.md)
- [CPL-007 基础与发布边界](docs/cpl-007-foundation.md)
- [ADR 0001：产品与仓库边界](docs/adr/0001-product-and-repository-boundary.md)
- [ADR 0002：原生属性图无旁路](docs/adr/0002-native-property-graph-no-bypass.md)
- [ADR 0003：性能缺口阻塞发布](docs/adr/0003-performance-gaps-block-release.md)
- [ADR 0004：.NET 宿主与 SonnetDB 固定依赖](docs/adr/0004-dotnet-host-and-source-dependency.md)

## 当前状态

C0 已完成，C1 处于实现中：CPL-010~012 已落地；CPL-013~015 在 source lane 已接通 active generation 原子切换、planning snapshot、query lease/cursor、writer fence、cutoff-aware cleanup、确定性提交边界故障重开、`workspace_status`、`code_search` exact/fulltext Preview 与 `symbol_get` 小型回归，`CG-005` 进入 verifying，`CG-007` 的 API/接线缺口已关闭。查询 cursor、fulltext filter plan、实时 watcher freshness、真实子进程 publish 前后 kill/reopen、Medium/Large 增量与双客户端门禁仍未通过，C1 不能标完成。默认 3.1.0 package 的 Native AOT 仍受 CG-006 限制；source lane 已使用修复后的 worker 路径，2026-08-29 win-x64 CLI publish/no-op smoke 已通过，但本轮 CG-007 变更尚未重新执行 Native AOT publish，7 天长稳仍待归档。C2 Preview、C3 Beta 和 C4 Production 继续受联合门禁约束。
