# Couplet

Couplet 是一个面向 Codex、Claude Code 等编码 Agent 的本地优先代码知识与上下文引擎。它把代码解析、增量索引、原生属性图、全文/向量混合检索和有预算的上下文组装连成一条可核验链路，并以嵌入式 `SonnetDB.Core` 作为唯一数据引擎。

> C0 基础与合同已于 2026-08-25 完成。C1 已实现工作区/Git 发现、filter-aware revision provenance、C#/TypeScript/JavaScript partial 适配器、增量规划和 SonnetDB Document/FullText staging。显式 `UseSonnetDbSource=true` 时，`index-stage` 已接入 generation 原子发布、cutoff cleanup 和真实子进程 commit 边界；daemon watcher 可持续 reconciliation，MCP 已接通 status、exact/fulltext、`symbol_get`。source store 以 database-root lease 保证单一 live owner，fulltext cursor 可在 orderly store reopen 后继续同一 retired revision，并对 terminal recovery fail closed。默认 package 查询、真实进程重启/跨进程 root/cursor 竞争、cursor hard-kill CAS、真实双客户端、Medium/Large 固定硬件与长稳门禁仍未完成。

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
dotnet run --project src/Couplet.Daemon -p:UseSonnetDbSource=true -- run --workspace . --database artifacts/c1-watch-db
dotnet run --project src/Couplet.Cli -- c1-capacity --repository . --scale medium --workspace artifacts/c1-medium/workspace --database artifacts/c1-medium/database --report artifacts/c1-medium/report.json
dotnet run --project src/Couplet.McpServer -p:UseSonnetDbSource=true -- serve --workspace . --database artifacts/c1-source-db
```

`workspace-scan` 只输出 workspace-relative path 和去凭证仓库身份。clean Git 的公开 `SourceRevision` 保持纯 HEAD，实际 included-input digest 独立进入 index revision；Git filters、CR/LF/leading-quote path、LFS-like smudge、父目录 symlink 和 tracked unreadable 均按 fail-closed 路径处理。默认 package lane 的 `index-stage` 继续只写 generation 独立 staging collection；source lane 原子发布 planning snapshot、Document 与 FullText，无变化时复用 active。source store 在打开 SonnetDB 前获取 `.couplet-store.lock`，同一 database root 同时只允许一个 live owner；构造失败和 dispose 会释放 lease，live backup 后续必须排除此 lock file。source/package one-shot 遇到 snapshot failure 均在 plan/stage/publish 前返回 `indexing_failed/workspace_snapshot_incomplete`。daemon watcher 合并文件事件，并以默认 30 秒 reconciliation 捕获 overflow、linked-worktree HEAD 和 assume-unchanged 变化；三次 fresh snapshot 均失败时保留旧 active。`--retired-generation-retention <c>` 控制 cleanup cutoff。真实 CLI commit 前后 kill/reopen 只证明本机确定性提交边界，不代表随机故障、7 天或固定硬件门禁。`c1-capacity` 两档 Performance/Capacity gate 仍为 FAIL；默认 package AOT 仍报告 CG-006，source 三个 win-x64 Native AOT publish 与原生 `version` smoke 已通过，但生产 journey/长稳未归档。

source lane 的 MCP Server 在同时显式提供 `--workspace` 与 `--database` 时，用真实 workspace discovery identity 打开数据库，并在完整 stdio host 生命周期内持有唯一 root-owning store。`workspace_status` 每次请求获取并释放一个 active generation lease，只读取同一 planning/manifest snapshot；查询不执行 Document 全扫。`code_search` exact 使用 `by_stable_id`，fulltext 通过 generation-bound FullText、planning/path/language/entity-kind 索引与 posting-stage filtered search 执行，共享访问预算并报告实际 access path。`symbol_get` 使用有界 stable/qualified identity 索引。fulltext opaque cursor 绑定完整 query shape、generation/revision、offset、nonce 和绝对到期时间；持久 HMAC key 与 `Available -> Claimed` CAS registry 允许 orderly store dispose/reopen 后通过 SonnetDB exact-revision lease 继续同一 retired generation。恢复清理以 version-CAS/snapshot/delete/snapshot 固定 terminal 状态；一次性 replay、坏记录及 CAS/删除/snapshot/timer 故障均 fail closed。lease 在成功清理或 dispose/reopen 时释放，timer fault 后可能暂时保留到该边界。当前仍无原生 search-after；深页继续使用有预算的 Top-K + offset。默认 package、`symbol_get` cursor、真实进程重启/跨进程 root/cursor 竞争与 cursor hard-kill CAS 仍 unavailable/`NOT_RUN`；CG-005 保持 verifying，CG-007 已关闭。

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

C0 已完成，C1 处于实现中：source lane 已接通 filter-aware revision provenance、daemon watcher、generation 原子切换/cleanup、真实 CLI commit 边界、status/search/symbol 查询、database-root 单 live owner 和 orderly store reopen 的 durable retired cursor terminal cleanup。本轮 source `177/177`、cursor `44/44`、package `90/90`，三个 win-x64 source Native AOT publish 与原生 `version` smoke 均通过。`CG-005` 仍为 verifying，C1 Correctness/Recovery 与 Performance/Capacity 均为 FAIL；真实进程重启/跨进程 root/cursor 竞争、cursor hard-kill CAS、真实双客户端、跨平台、随机故障、固定硬件 Medium/Large、生产 AOT journey 与 7 天长稳仍未验证。C2 Preview、C3 Beta 和 C4 Production 继续受联合门禁约束。
