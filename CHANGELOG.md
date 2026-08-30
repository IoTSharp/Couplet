# Changelog

本项目的所有重要变更都会记录在此文件中。

## [Unreleased]

### Added

- 建立 Couplet 独立仓库与 C0-C4 路线基线，明确面向 Codex、Claude Code 等编码 Agent 的本地代码知识与上下文产品定位。
- 冻结 `Couplet -> SonnetDB.Core` 单向依赖、SonnetDB 唯一数据引擎和原生属性图无旁路原则。
- 增加 typed、只读 MCP v1 合同，以及工作区、搜索、符号关系、依赖路径、影响分析、变更上下文和 context pack 工具边界。
- 增加 golden journeys、质量/性能双门禁、能力缺口目录和跨仓 C0-C4 / SonnetDB M40 发布映射。
- 增加文档基线 CI 和本地路线校验脚本。
- 增加 .NET 10 `.slnx`，分离 Core、Application、SonnetDB adapter、CLI、daemon 与 MCP Server 进程边界，并提供版本、能力报告和可取消 daemon 生命周期（CPL-007）。
- 增加 source-generated JSON、trim/AOT analyzer、依赖边界与启动生命周期自动化测试，以及逐 executable/worker 发布能力矩阵（CPL-007）。
- 冻结 `couplet.code_graph.v1` stable ID、provenance、confidence、generation 发布与删除合同，并增加 JSON Schema 和不变量测试（CPL-002）。
- 实现 `couplet.sonnetdb_handshake.v1`，分离 public API 联调状态、发布等级与 blocking gap（CPL-003）。
- 增加 Small/Medium/Large 多语言 fixture、golden answers、确定性生成器、C0 benchmark/evidence runner 和 Codex/Claude Code paired eval runner（CPL-004）。
- 增加本地优先安全、ignore/deny、显式在线 provider 与数据生命周期合同（CPL-005）。
- 实现八个 typed、只读 MCP v1 schema，以及 stdio initialize、schema discovery、预算/取消、HMAC cursor、稳定错误和 unavailable tool call（CPL-006）。
- 实现 canonical workspace/Git 发现、ignore/deny、symlink/binary/generated/large-file 策略和有界文件变更监听（CPL-010）。
- 增加可替换 C#、TypeScript/JavaScript lexical partial 适配器，以及 stable file/symbol/chunk ID、UTF-8 source span、provenance、confidence 和符号边界 chunk（CPL-011/CPL-012）。
- 增加版本化 C1 C#/TypeScript/JavaScript 语言 fixture 与完整 golden snapshot，冻结 Unicode UTF-8 span、同名/重载、generic lexical 边界、stable symbol/chunk、provenance 和声明置信度；适配器能力仍为 `Partial`。
- 增加 `couplet.indexing.v1` 机器合同、初次 snapshot、rename/modify/delete 增量计划和 parser/producer upgrade rebuild 判定合同；默认 package lane 的 `index-stage` runtime 仍以空 previous snapshot 执行全量 staging（CPL-013/CPL-015）。
- 使用固定 `SonnetDB.Core 3.1.0` 实现 generation 独立 Document/FullText staging、path/fulltext index、批量 source-generated JSON 写入、一致性校验、checkpoint/reopen 和确定性 retry（CPL-014/CPL-015）。
- CLI 增加 `workspace-scan` 与 `index-stage`；staging 报告显式返回 `published=false` 和 `CG-005`，不向 MCP 暴露未发布数据。
- 增加 Git branch/HEAD snapshot 身份、真实 branch switch 强制重建与跨分支 staging 隔离回归（CPL-013）。
- 增加 staging completion marker 写入顺序、missing/corrupt marker 重开检查、checkpoint-budget 安全 retry 和 `couplet.staging_inspection.v1`（CPL-015）。
- 增加 `c1-capacity`、`couplet.c1_capacity_evidence.v1` 与固定双语言 Medium/Large 语料；真实结果如实记录首次/增量、query、reopen、RSS、allocation 和数据库增长，当前 Performance/Capacity gate 为 FAIL。
- 增加 Codex/Claude Code 对三个 C1 MCP 工具的端到端 golden gate，证明未发布 staging 不进入 `workspace_status`、`code_search` 或 `symbol_get`。
- 增加显式 `UseSonnetDbSource=true` 联调 lane，以最新 SonnetDB `Tsdb.Generations` 原子发布 planning KV、Document 与 FullText 资源，并覆盖 active lease、generation-bound cursor、重开和 lease-aware cleanup（CPL-013/CPL-015）。
- 增加 source lane publish 提交边界的 internal、默认无行为故障点与重开回归：提交前故障保持完整旧 generation 并可重试，提交后故障保持完整新 generation、exact/FullText 新 revision 可见且重试复用 active revision（CPL-015）；真实子进程 kill 仍是门禁缺口。
- source lane MCP 在显式 `--database` 下接通 `workspace_status`：每个请求持有单一 active generation lease，从同一 planning/manifest snapshot 返回 typed、source-generated 响应；覆盖空库、无 Document 全扫、重开、新 active revision、旧 selector fail closed、损坏 manifest、deadline 序列化边界和完整 stdio 宿主生命周期。C2/C3 工具仍 unavailable，CG-005 保持开放。
- source lane MCP 接通 `code_search` exact/fulltext 首切片：每个请求持有 active generation lease，exact 使用 `by_stable_id` Document path index，fulltext 使用 generation-bound `code_search` FullText index，并返回 typed、source-generated 响应及实际 access path/候选计数/预算诊断。source capability 升为 Preview；默认 package lane 保持 `generation_publish_blocked`，查询 cursor、fulltext filter plan、真实进程恢复和容量门禁仍未完成，CG-005 保持 verifying。
- source lane MCP 接通 fulltext `code_search` 同 active generation cursor：opaque cursor 绑定 query shape、generation/revision 与 little-endian offset，篡改、形状变化、负值/溢出和 generation 切换分别稳定 fail closed；分页只调用 generation-bound FullText Top-K 并 hydration 当前页，自动化证明 Document full-scan counter 不变。当前 Core FullText 没有原生 search-after，深页超过候选预算返回 `budget_exhausted`，不隐藏 scan fallback；cursor 不跨请求保留 retired generation lease，CG-005 与 C1 双门禁保持 verifying/FAIL。
- source lane MCP 接通 `symbol_get`：stable symbol ID 使用唯一 `by_stable_id`，qualified identity + language 通过稳定 ID 派生走同一唯一索引，无 language 时以 `by_qualified_identity` 最多读取两条并对歧义 fail closed；响应在 active lease 内携带 signature、confidence、source evidence、实际 access path 和预算诊断，不使用 Document 全扫。
- source lane 接入 SonnetDB cutoff-aware retired cleanup，CLI `--retired-generation-retention` 真实控制到期资格；新增 zero/due/mixed-age reopen/lease/cancellation/failure/最大时长/连续发布回归并关闭 CG-007 的 API/接线缺口。
- 新增 `couplet.index_stage.v2` source-lane stage report 与独立 schema，分别报告 lease-deferred 和 retention-deferred revisions；封闭的 v1 schema 与固定 package v1 JSON 保持不变。

### Changed

- source lane 的 `index-stage` 从 active generation 读取轻量 planning snapshot，传递真实 previous revision；默认 `SonnetDB.Core 3.1.0` package lane 与其 lock file 保持独立且继续诚实返回未发布 staging。source restore/build 使用按项目隔离的 `artifacts/obj/sonnetdb-source` 与 `artifacts/bin/sonnetdb-source`，不会被源码 glob 纳入编译，也不会改写默认 package locks 或输出。
- 最新 SonnetDB source lane 启用已修复的 Native AOT background worker 生命周期；只有默认 3.1.0 package 的 AOT 路径继续禁用 worker 并报告 CG-006。普通 source/JIT handshake 不再把选择源码依赖外推为 AOT 已验证；2026-08-29 source AOT CLI 已完成 0 IL/AOT warning 的首次 publish 与 unchanged no-op smoke，本轮 CG-007 retention 变更尚未重新执行 Native AOT publish，7 天长稳仍待归档。source runtime 的 retention cutoff 已接线；默认零值保持原立即清理行为。
- 将 lexical partial adapter 的声明置信度从需要语言语义证明的 `Exact/1.0` 更正为带稳定词法规则证据的 `Inferred/0.9`，同时把 adapter producer version 从 `1.0.0` 升至 `1.1.0`、规则升至 `declaration.v2`，保证新 snapshot 的 revision/provenance 反映生产者变化。`IncrementalIndexPlanner` 已覆盖 `1.0.0 -> 1.1.0` 的 `producer_version_changed` 合同；当前 `index-stage` runtime 尚不读取旧 snapshot/manifest，不能据此宣称真实运行路径会按版本差异触发增量重建。
- 默认依赖从临时 SonnetDB 源码引用切换为官方 `SonnetDB.Core 3.1.0` 固定 package 和 content hash；Couplet 可独立 checkout 构建，但产品发布仍受 C1-C4 功能门禁约束。
- 默认 package lane 的 C1 能力门控从“未实现”细化为“staging 已实现、generation 发布受阻”；exact/fulltext MCP 继续返回稳定 `capability_unavailable`，原因码为 `generation_publish_blocked`。source lane exact/fulltext 与 `symbol_get` 已升为 Preview；fulltext `code_search` 已接通同 active generation cursor，跨 generation 续页和 `symbol_get` cursor 继续使用稳定 capability limitation。
- Native AOT 的 `index-stage` 通过固定包公开 options 关闭不兼容的 background flush/compaction/retention/KV maintenance worker，并在 staging/handshake 中显式报告 CG-006；JIT 路径继续启用默认后台维护。
- `workspace_status` 不扫描 Document 行、不重算 generation checksum、不触发 rebuild；它只校验 active lease 资源角色、确定性资源名、schema、manifest checksum 形状和 planning identity。`source_revision` 与 `database_bytes` 是 MCP 启动快照，diagnostics 显式报告该 freshness 边界；source lane exact/fulltext 初始化能力在 active query 首切片接线后声明 Preview，固定 package lane 继续 unavailable。

### Fixed

- 无源码或 producer 变化的 source-lane `index-stage` 重跑复用 active generation，不再尝试删除或重写 SonnetDB 已受 generation ownership 保护的 collection。
- `index-stage` writer fence 现在覆盖锁内 fresh discovery 到 active read/build/plan/publish，等待可取消；发布已提交后的 retired cleanup 失败保持 `published=true` 并返回稳定 retry limitation，预取消 no-op 不会触发 cleanup。
- SonnetDB Document 批次累计超过 checkpoint budget 时，只对固定包保证“WAL append 前拒绝”的稳定异常执行 checkpoint 后原批次 retry，避免中大型 staging 因 admission budget 提前失败。
