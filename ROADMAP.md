# Couplet 路线图

> 基线日期：2026-08-10。仓库、产品边界、跨仓依赖和 C0-C4 交付路线已经冻结；C0 基础与合同已完成，C1-C4 产品能力仍为计划状态，不能据此宣称索引、图或混合检索已经可用。

## 状态

| 项目 | 状态 | 证据 |
|---|---|---|
| GitHub 独立仓库 | ✅ 已完成 | `https://github.com/IoTSharp/Couplet` |
| 产品名与职责边界 | ✅ 已完成 | `README.md`、ADR 0001 |
| `Couplet -> SonnetDB.Core` 单向依赖决策与合同 | ✅ 基线已冻结 | `docs/architecture.md`、ADR 0001；实际 package/capability 接线属于 CPL-007 |
| C0-C4 路线、阻塞关系与退出门禁 | ✅ 已完成 | 本文、`docs/quality-gates.md` |
| 原生属性图无旁路决策 | ✅ 已完成 | ADR 0002、`docs/capability-gaps.md` |
| 性能缺口优先回收决策 | ✅ 已完成 | ADR 0003、`docs/quality-gates.md` |
| Couplet C0 基础与合同 | ✅ 已完成 | 版本化 graph/generation/security/MCP 合同、fixture/eval runner、35 个自动化测试和 stdio smoke |
| Couplet 可执行产品 | 🚧 C1 实现中 | source generation 原子发布、cutoff-aware cleanup、提交边界故障重开和 MCP exact/fulltext Preview 首切片已接线；symbol/cursor、真实进程 kill、capacity 与 7 天长稳仍受门禁约束，默认 package AOT 仍受限 |
| SonnetDB 最新源码联调 | 🚧 generation API 已接线 | 显式 source ProjectReference 已接入 `Tsdb.Generations`；默认 `SonnetDB.Core 3.1.0` package 仍作为独立构建基线，M40/Couplet 联合门禁尚未通过 |

状态符号只描述本行结果；“路线已完成”不等于“路线中的产品能力已完成”。

## 不可变原则

1. **单向依赖**：Couplet 通过稳定的嵌入式 API 使用 `SonnetDB.Core`；SonnetDB 不引用 Couplet 的代码、schema 或产品合同。
2. **一套数据引擎**：工作区元数据、文档、全文、向量和图关系均由 SonnetDB 的模型原生能力承担，不引入第二套数据库或索引真相源。
3. **原生属性图**：定义、引用、调用、继承、包含、依赖和测试覆盖关系必须进入原生 GraphStore；不得用关系边表或应用层遍历代替。
4. **没有固定语料上限**：索引入口不设置 5,000 词或相似硬限制；容量只由资源预算和公开容量证据约束。单次结果必须分页、有预算、可取消、可诊断。
5. **缺口回收到 Core**：通用正确性、恢复、访问路径或性能缺口归属 SonnetDB；Couplet 可以缩小能力声明或 fail fast，但不能建立旁路。
6. **先证据后宣称**：每个阶段分别通过 correctness/recovery 和 performance/capacity gate；任一失败都不能把本阶段标为完成、发布依赖能力或改变产品文案，但不阻止针对已就绪 public API 继续联调取证。
7. **本地优先与最小暴露**：默认不把仓库内容发送到外部服务；在线 embedding 或模型能力必须显式选择，并记录 provider 与数据边界。

## 跨仓联合门禁

SonnetDB 阶段 gate 需要 Couplet 工作负载才能验收，因此“可联调条件”和“退出/发布门禁”必须分开，双方不能互相等待：

| Couplet 阶段 | 使用的 SonnetDB 能力 | 联调/开发开始条件 | 联合退出/发布门禁 | 未通过时的行为 |
|---|---|---|---|---|
| C0 | 嵌入式数据库生命周期与稳定 API | 仓库/路线基线已建立 | Couplet 合同/eval runner 与 M40 `#341` workload/SLO 同时冻结 | ✅ 已冻结 `c0-handshake.v1`；不宣称图能力 |
| C1 | KV、Document、FullText、generation/snapshot/recovery | 最新源码 `Tsdb.Generations` public API 可用 | Couplet revision/crash/cursor/retention/capacity gate 全 PASS | source lane 可继续发布/租约联调；MCP 与发布门禁未通过时不开放索引查询 |
| C2 | 原生 GraphStore、邻接、属性索引、流式路径和诊断 | M40 `#347~#351` 目标 public API 可联调 | M40 `#352` 与 Couplet C2 correctness/performance 同时 PASS，才发布 Preview | 图工具保持 unavailable/内部联调，不做关系表或内存遍历降级 |
| C3 | FullText、Vector、Graph 的共享 typed hybrid plan | M40 `#353~#358` 目标 API 与相关 M35/M36 能力可联调 | M40 `#359` 与 Couplet C3 Agent/检索 gate 同时 PASS，才发布 Beta | 不在 Couplet 内合并候选或扩图，不隐藏 scan fallback |
| C4 | 生产图快照、维护、恢复和固定硬件容量 | M40 `#360~#366` 目标能力可联调 | M40 `#367` 与 Couplet C4 长稳/安全/容量 gate 同时 PASS，才发布 1.0 | 不发布 Production/GA，不提高默认资源上限掩盖缺口 |

`#352`、`#359`、`#367` 指 SonnetDB [原生属性图路线](https://github.com/IoTSharp/SonnetDB/blob/main/docs/native-graph-database-roadmap.md)中的交付编号。

## C0：基础与合同

目标：建立可运行但不虚报能力的产品骨架，让后续实现共享同一套 schema、预算、评测和证据格式。

交付编号使用稳定的 `CPL-NNN`，不与 GitHub issue 序号绑定。

交付物：

- **CPL-001**：✅ 产品/仓库 ADR、双方责任、非目标和无旁路条款。
- **CPL-002**：✅ 代码 graph schema、stable ID、provenance、generation 发布与删除合同。
- **CPL-003**：✅ SonnetDB capability/version matrix 和 C0-C4/M40 发布 handshake。
- **CPL-004**：✅ 多语言 Small/Medium/Large fixture manifest、golden answers、benchmark runner 和 paired Agent eval runner。
- **CPL-005**：✅ 安全、隐私、ignore/deny、provider 和数据生命周期合同。
- **CPL-006**：✅ 八个 typed、版本化、首版只读的 MCP schema；统一 initialize workspace handshake、错误、分页、预算、新鲜度和 evidence 模型。
- **CPL-007**：✅ .NET 10 solution、CLI/daemon/MCP Server 边界、`SonnetDB.Core 3.1.0` 固定 package、dependency/trim/AOT 验证和按 executable/worker 的发布能力矩阵；不发布空壳包。

退出门禁：

- 同一 request 在 schema 兼容版本内产生确定性的响应结构和稳定错误码。
- 空工作区、损坏/旧版数据库、取消、预算耗尽和 provider 不可用均有自动化测试。
- [质量与性能门禁](docs/quality-gates.md)中的语料、硬件指纹、指标和证据格式可由 CI/本地 runner 生成。
- Couplet 合同/eval runner 与 SonnetDB `#341` 对代码知识 golden journey、语料和预冻结 SLO 达成同一版本记录；未满足时 C0 不标完成，但不阻止双方继续实现已冻结的非冲突地基。

状态：✅ 已完成（2026-08-25）。联合版本记录见 `contracts/c0-handshake.v1.json`；Release 0 warning/0 error、35 个测试、CLI evidence 和 stdio MCP smoke 已通过。该状态只表示合同与 runner 完成，C1-C4 产品能力仍按各自门禁推进。

## C1：增量代码索引

目标：在不依赖图能力宣称的前提下，可靠建立工作区、Git、文件、chunk、符号定义和全文检索地基。

交付物：

- **CPL-010**：✅ 已实现工作区发现、canonical path、Git revision/branch/worktree、Git ignore、deny/ignore 优先级、symlink 边界、binary/generated file 和大文件 text-only 策略。
- **CPL-011**：✅ 已实现可替换语言适配器与 Semantic Tier；首批 C#、TypeScript/JavaScript 明确为 lexical `Partial`，unsupported/large input 明确为 `TextOnly`，不宣称完整语义；版本化 C1 fixture 冻结同名、重载和当前不支持 generic method 的边界，lexical adapter `1.1.0` 将声明 confidence 固定为 `Inferred/0.9`。
- **CPL-012**：✅ 已实现 stable file/symbol/chunk ID、UTF-8 byte/line/column source span、content hash、provenance、adapter version、confidence 和符号边界 chunk；完整 golden snapshot 覆盖三种语言与 Unicode source evidence。
- **CPL-013**：🚧 已实现初次 snapshot、文件监听、content-hash rename、修改/删除、producer/branch rebuild 判定和跨分支隔离。source lane runtime 从 active generation 读取轻量 planning snapshot，传递真实 previous revision，并以 no-op 复用无变化 active generation；默认 package lane 仍只做全量 staging。
- **CPL-014**：🚧 已实现 generation 独立的 SonnetDB Document/FullText staging、path/fulltext index 校验和实际访问路径探针；source lane 将 planning KV、Document 与 FullText 原子发布，并在显式数据库 MCP 宿主中用单一 per-request active lease 提供 typed `workspace_status` 与 `code_search` exact/fulltext Preview。exact 命中 `by_stable_id` path index，fulltext 命中 generation-bound FullText index，均返回实际 access path 与有界诊断；`symbol_get`、查询 cursor 和 fulltext filter plan 仍 unavailable。
- **CPL-015**：🚧 已实现解析失败报告、取消、completion marker、checkpoint retry、staging consistency/reopen，以及 source lane publish/reopen、generation-bound cursor、writer fence、lease-aware/cutoff-aware retired cleanup、cleanup failure 隔离、publish 提交边界故障回归和 `workspace_status` 重开/revision selector fail-closed 回归。`--retired-generation-retention` 把生命周期时长传给可控 UTC cutoff，mixed-age 重开只删除到期且无 lease 的 revision；零值保持立即清理。CG-007 的 Core API/接线缺口已关闭。真实子进程 kill-before/after-publish、查询 cursor continuity 和容量回归未完成。

退出门禁：

- golden corpus 中已声明支持的定义和 source span 零 mismatch；partial 语义不伪装为 exact。
- crash/restart 不暴露混合 revision，删除/重命名/branch switch 后无孤儿文档或旧命中。
- 中/大仓初始与增量索引、exact lookup 和全文查询达到冻结 SLO；不存在未解释的全库重扫。
- Codex 与 Claude Code 能读取相同合同，所有结果都可回到文件、revision 和 source span。
- SonnetDB `#343/#346` 所需 snapshot lease/cursor/recovery public contract 与 Couplet generation 发布、query lease 和清理回归同时通过；对应 CG-005 关闭。

状态：🚧 实现中（2026-08-30）。CPL-010~012 已落地；CPL-013~015 的 source lane generation publish/acquire/cursor/cutoff cleanup、writer fence、no-op/reopen、确定性提交边界故障、`workspace_status` 与 `code_search` exact/fulltext Preview 小型回归已实现，`CG-005` 进入 verifying，`CG-007` 的选择性 cleanup API/接线缺口已关闭。`symbol_get`、查询 cursor、fulltext filter plan、实时 watcher freshness、真实子进程 publish kill、双客户端和 Medium/Large 联合门禁仍未通过；Correctness/Recovery 与 Performance/Capacity 均保持 FAIL。默认 3.1.0 package 的 Native AOT 仍报告 CG-006；source lane 使用已修复的 worker 生命周期，2026-08-29 win-x64 CLI publish/no-op smoke 已通过，但本轮 CG-007 变更尚未重新执行 Native AOT publish，7 天长稳仍待归档。详见 [C1 增量索引实现与证据](docs/c1-indexing-evidence.md)和 [C1 Medium/Large 容量证据](docs/c1-capacity-evidence.md)。

## C2：原生图代码智能

目标：使用 SonnetDB 原生属性图交付可解释、可取消、有界的代码关系与影响分析。`#347~#351` public API 可用后即可并行联调，`#352` 是双方共同产出证据后的退出门禁，不是开发前置。

交付物：

- **CPL-020**：File/Module/Namespace/Type/Member/Test/BuildTarget 等节点，以及 Contains/Defines/References/Calls/Imports/Inherits/Implements/Covers/DependsOn 等版本化边。
- **CPL-021**：解析 revision 内节点、双向邻接、属性索引和派生状态的原子提交与恢复。
- **CPL-022**：`symbol_relations`、`dependency_path`、`impact_analyze`、`change_context`；返回路径、方向、深度、置信度和逐跳证据。
- **CPL-023**：bounded depth/frontier/result/deadline，cycle、self-loop、parallel edge、supernode 和跨语言未知边界处理。
- **CPL-024**：Git diff 到受影响符号、调用者、构建目标与候选测试的可解释映射。

退出门禁：

- SonnetDB `#352` 两个 gate 全部 PASS；Couplet 语料包含在固定硬件报告中。
- 每条关系和影响结论都带 revision 与 source evidence；golden graph 零 orphan/index drift。
- 1-6 hop、依赖路径和影响分析的复杂度随命中邻接/frontier 增长，不随全图规模线性增长；计划和实际计数一致。
- 不存在关系表、应用层 BFS/DFS、第二图引擎或不可见 scan fallback。

状态：📋 计划；Preview 发布受 SonnetDB `#352` + Couplet C2 联合门禁约束。

## C3：混合检索与 Context Pack

目标：把精确匹配、全文、向量和图邻域组合成高密度、可引用、受 token 预算控制的 Agent 上下文。`#353~#358` public API 可用后并行联调，`#359` 由双方 journey 联合验收。

交付物：

- **CPL-030**：本地 embedding 默认路径；在线 provider 仅在用户显式配置后启用，cache key 包含 model/version/content hash。
- **CPL-031**：chunk 生命周期与文件/symbol/revision 一致；更新和删除不会留下旧向量或全文命中。
- **CPL-032**：把检索意图、过滤和预算提交给 SonnetDB 单一 shared typed hybrid plan，由 Core 完成 candidate access、去重/融合和 Native Graph expansion；Couplet 只对返回的有界统一候选做 context selection，可选 rerank 不得重新召回、逐跳或替代 `#359`。
- **CPL-033**：`context_pack` 按任务、diff、入口符号和预算选择定义、调用者、约束、测试与证据，不输出重复大段源码。
- **CPL-034**：Python、Java、Go 逐个通过 Semantic Tier fixture 后启用，不把“文件可索引”表述为语义支持。
- **CPL-035**：paired Codex/Claude Code eval，覆盖定位、修改、影响分析、测试选择和大仓上下文任务。

退出门禁：

- SonnetDB `#359` 及相关 M35/M36 gate 通过；组合查询不在产品层全量 merge 或遍历。
- recall/precision、引用正确性、token、延迟和成本全部进入版本化报告；截断始终可见且可续页。
- 相同模型/版本/提示下，paired eval 同时满足质量下限和效率改进目标，不以减少必要证据换取 token 数字。
- 本地模式通过断网测试；在线模式可审计发送范围且不会泄露被 ignore/deny 的内容。

状态：📋 计划；Beta 发布受 SonnetDB `#359` + Couplet C3 联合门禁约束。

## C4：生产与 Agent 体验

目标：完成大仓、长时间运行、升级恢复、安全和分发验收，形成 Codex 与 Claude Code 可日常依赖的本地服务。与 SonnetDB `#360~#366` 并行压测和补缺，双方证据共同关闭 `#367`。

交付物：

- **CPL-040**：跨平台安装、升级、卸载、数据库迁移/重建、单实例锁、daemon 生命周期和诊断包。
- **CPL-041**：多 worktree/monorepo、大仓增量队列、背压、资源配额、取消和优雅关闭。
- **CPL-042**：Codex 与 Claude Code 安装/连接/健康检查、版本协商、最小权限、安全审计与 SBOM/签名。
- **CPL-043**：7 天 mixed workload、kill/reopen、backup/restore、损坏检测、SonnetDB Core Native AOT，以及 Couplet 各 executable/worker 按 CPL-007 能力矩阵执行的 trim/AOT 与固定硬件容量报告。
- **CPL-044**：发布能力矩阵、语言等级、已知限制、gap catalog 和可复现 Agent eval 报告。

退出门禁：

- SonnetDB `#367` 全部 PASS，所有 Production 阻塞 gap 关闭。
- 小/中/大仓 correctness/recovery 与 performance/capacity gate 分别通过；无静默陈旧、截断或 fallback。
- Codex 与 Claude Code golden journeys 在支持的平台上端到端通过，升级和回滚不会损坏现有索引。
- 只有上述证据齐全后才能使用 Production/GA 表述。

状态：📋 计划；Production/1.0 发布受 SonnetDB `#367` + Couplet C4 联合门禁约束。

## 执行顺序

```text
仓库/路线基线（已完成）
          |
   C0 <------> SonnetDB #341             -> 联合 C0 gate
          |
   C1 <------> SonnetDB #342~#346        -> C1 revision/recovery gate
          |
   C2 <------> SonnetDB #347~#351        -> 联合 #352 / Preview gate
          |
   C3 <------> SonnetDB #353~#358        -> 联合 #359 / Beta gate
          |
   C4 <------> SonnetDB #360~#366        -> 联合 #367 / 1.0 gate
```

同一阶段内的修复优先级固定为：正确性/原子性/恢复 -> 有界执行与消除非预期全扫/物化 -> 容量/延迟/资源放大 -> 新 API、集成和 UI。阶段日期只在前置证据可预测后设置，不以日历日期覆盖质量门禁。
