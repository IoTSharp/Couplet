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
- 增加 source lane publish 提交边界的 internal、默认无行为故障点与重开回归：提交前故障保持完整旧 generation 并可重试，提交后故障保持完整新 generation、exact/FullText 新 revision 可见且重试复用 active revision（CPL-015）。新增真实 `Couplet.Cli` 子进程 commit 前/后暂停、强杀与重开回归；测试 hook 必须由显式环境变量启用，否则 CLI 以稳定错误 fail closed。重开后逐项对拍全部 Document exact 记录、FullText 命中 ID 集合、资源绑定和实际总量，后续真实 CLI retry/no-op/cleanup 证明只能看到完整旧或完整新 revision；该本机确定性证据不外推为断电/fsync、长稳或固定硬件 PASS。
- source lane MCP 在显式 `--database` 下接通 `workspace_status`：每个请求持有单一 active generation lease，从同一 planning/manifest snapshot 返回 typed、source-generated 响应；覆盖空库、无 Document 全扫、重开、新 active revision、旧 selector fail closed、损坏 manifest、deadline 序列化边界和完整 stdio 宿主生命周期。C2/C3 工具仍 unavailable，CG-005 保持开放。
- source lane MCP 接通 `code_search` exact/fulltext 首切片：每个请求持有 active generation lease，exact 使用 `by_stable_id` Document path index，fulltext 使用 generation-bound `code_search` FullText index，并返回 typed、source-generated 响应及实际 access path/候选计数/预算诊断。source capability 升为 Preview；后续切片已补 cursor、fulltext filter plan 与真实 CLI commit 边界 kill/reopen，默认 package lane 仍保持 `generation_publish_blocked`；进程重启 cursor、随机故障和容量门禁未完成，CG-005 保持 verifying。
- source lane MCP 接通 fulltext `code_search` 同进程 retained-generation cursor：opaque cursor 绑定 query shape、generation/revision、little-endian offset 与随机 nonce；每条续页链以最多 128 个、默认两分钟绝对 TTL 的进程内 lease 跨请求保留 retired generation，一次性转移并在最终页、错误、取消、到期、容量拒绝或 store dispose 时释放。主动 timer 防止 idle lease 超期滞留；容量在 fulltext 查询前预留并 fail fast，exact unique lookup 不占 slot。对抗回归额外覆盖响应 IOException、store dispose/reopen cleanup，以及终页释放后 slot 复用。分页继续只调用 generation-bound FullText Top-K 并 hydration 当前页，自动化证明 Document full-scan counter 不变；进程重启后的 cursor 仍 fail closed，CG-005 与 C1 双门禁保持 verifying/FAIL。
- source lane fulltext `code_search` 接通 path/language/entity-kind 过滤计划：generation schema 新增 `by_language` 与稀疏 `by_entity_kind`，path glob 以有界 planning snapshot 匹配后走 `by_path`，各索引候选求交后交给 SonnetDB posting-stage filtered search；planning、filter candidate 与 posting visit 共用单次查询预算，耗尽时返回稳定 `budget_exhausted` 且不泄漏部分命中。access path 显式报告 planning snapshot、各 path index 与 `document_fulltext_filtered`，cursor 绑定完整过滤 shape；旧 active generation 缺新索引时不再 no-op 复用，而是发布新 ordinal generation。CG-005 仍为 verifying，进程重启 cursor、固定硬件 capacity、实时 watcher 和双客户端证据未完成。
- source lane MCP 接通 `symbol_get`：stable symbol ID 使用唯一 `by_stable_id`，qualified identity + language 通过稳定 ID 派生走同一唯一索引，无 language 时以 `by_qualified_identity` 最多读取两条并对歧义 fail closed；响应在 active lease 内携带 signature、confidence、source evidence、实际 access path 和预算诊断，不使用 Document 全扫。
- source lane 接入 SonnetDB cutoff-aware retired cleanup，CLI `--retired-generation-retention` 真实控制到期资格；新增 zero/due/mixed-age reopen/lease/cancellation/failure/最大时长/连续发布回归并关闭 CG-007 的 API/接线缺口。
- 新增 `couplet.index_stage.v2` source-lane stage report 与独立 schema，分别报告 lease-deferred 和 retention-deferred revisions；封闭的 v1 schema 与固定 package v1 JSON 保持不变。
- source lane fulltext cursor 改为持久 HMAC key 与 `Available -> Claimed` CAS registry，绝对 TTL 和 128-slot 上限跨 orderly store reopen 保持不变；重开后通过 SonnetDB exact-revision lease 继续读取同一 retired generation，一次性 replay、坏记录/无效 generation metadata、未知 CAS 结果、timer 和最终 release 故障均 fail closed。该实现只证明同机顺序关闭/重开和进程内 fault hooks；真实进程重启/跨进程 cursor、hard-kill CAS 窗口及双客户端竞争仍未取证。
- daemon `run --workspace ... --database ...` 接通 FileSystemWatcher + 默认 30 秒 reconciliation：rename/delete、队列溢出 full rescan、linked worktree HEAD 变化、assume-unchanged 内容变化、三次 fresh snapshot retry、writer fence 释放和提交后输出失败均有回归；数据库物理路径解析后必须位于 workspace 外。workspace revision provenance 现在以 filter-aware Git blob 对拍 clean HEAD，raw included-input digest 进入 index revision，逐组件拒绝越界 symlink，tracked `Unreadable`/`SymlinkOutside` 转为 snapshot failure；source/package one-shot 在 plan/stage/publish 前返回 `indexing_failed/workspace_snapshot_incomplete`。

### Changed

- workspace/watch fingerprint 与 included-input digest 改为 `IncrementalHash` + pooled UTF-8 buffer，binary probe 复用 `ArrayPool<byte>`；Git filter writer/parser/stderr/exit 并发观察，异常统一 kill-tree、wait 并观察全部 task。clean Git 的公开 `SourceRevision` 保持纯 HEAD；subdirectory scope 进入公开 `WorktreeIdentity`，使 `WorkspaceId` 可由公开 identity 重算。
- source lane 的 `index-stage` 从 active generation 读取轻量 planning snapshot，传递真实 previous revision；默认 `SonnetDB.Core 3.1.0` package lane 与其 lock file 保持独立且继续诚实返回未发布 staging。source restore/build 使用按项目隔离的 `artifacts/obj/sonnetdb-source` 与 `artifacts/bin/sonnetdb-source`，不会被源码 glob 纳入编译，也不会改写默认 package locks 或输出。
- 最新 SonnetDB source lane 启用已修复的 Native AOT background worker 生命周期；只有默认 3.1.0 package 的 AOT 路径继续禁用 worker 并报告 CG-006。普通 source/JIT handshake 不再把选择源码依赖外推为 AOT 已验证；本轮 source CLI、Daemon 与 MCP Server 的 win-x64 Native AOT publish 均为 0 个未处置 IL/AOT warning，CLI 的 2026-08-29 publish/no-op runtime smoke 仍是当前最新实跑，7 天长稳尚待归档。source runtime 的 retention cutoff 已接线；默认零值保持原立即清理行为。
- 将 lexical partial adapter 的声明置信度从需要语言语义证明的 `Exact/1.0` 更正为带稳定词法规则证据的 `Inferred/0.9`，同时把 adapter producer version 从 `1.0.0` 升至 `1.1.0`、规则升至 `declaration.v2`，保证新 snapshot 的 revision/provenance 反映生产者变化。`IncrementalIndexPlanner` 已覆盖 `1.0.0 -> 1.1.0` 的 `producer_version_changed` 合同；当前 `index-stage` runtime 尚不读取旧 snapshot/manifest，不能据此宣称真实运行路径会按版本差异触发增量重建。
- 默认依赖从临时 SonnetDB 源码引用切换为官方 `SonnetDB.Core 3.1.0` 固定 package 和 content hash；Couplet 可独立 checkout 构建，但产品发布仍受 C1-C4 功能门禁约束。
- 默认 package lane 的 C1 能力门控从“未实现”细化为“staging 已实现、generation 发布受阻”；exact/fulltext MCP 继续返回稳定 `capability_unavailable`，原因码为 `generation_publish_blocked`。source lane exact/fulltext 与 `symbol_get` 已升为 Preview；fulltext `code_search` 已接通同进程 active/retired generation cursor，进程重启续页和 `symbol_get` cursor 继续使用稳定 capability limitation。
- Native AOT 的 `index-stage` 通过固定包公开 options 关闭不兼容的 background flush/compaction/retention/KV maintenance worker，并在 staging/handshake 中显式报告 CG-006；JIT 路径继续启用默认后台维护。
- `workspace_status` 不扫描 Document 行、不重算 generation checksum、不触发 rebuild；它只校验 active lease 资源角色、确定性资源名、schema、manifest checksum 形状和 planning identity。`source_revision` 与 `database_bytes` 是 MCP 启动快照，diagnostics 显式报告该 freshness 边界；source lane exact/fulltext 初始化能力在 active query 首切片接线后声明 Preview，固定 package lane 继续 unavailable。

### Fixed

- 无源码或 producer 变化的 source-lane `index-stage` 重跑复用 active generation，不再尝试删除或重写 SonnetDB 已受 generation ownership 保护的 collection。
- `index-stage` writer fence 现在覆盖锁内 fresh discovery 到 active read/build/plan/publish，等待可取消；发布已提交后的 retired cleanup 失败保持 `published=true` 并返回稳定 retry limitation，预取消 no-op 不会触发 cleanup。
- SonnetDB Document 批次累计超过 checkpoint budget 时，只对固定包保证“WAL append 前拒绝”的稳定异常执行 checkpoint 后原批次 retry，避免中大型 staging 因 admission budget 提前失败。
