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
- 增加 `couplet.indexing.v1` 机器合同、初次 snapshot、rename/modify/delete 增量计划和 parser/producer upgrade 确定性重建（CPL-013/CPL-015）。
- 使用固定 `SonnetDB.Core 3.1.0` 实现 generation 独立 Document/FullText staging、path/fulltext index、批量 source-generated JSON 写入、一致性校验、checkpoint/reopen 和确定性 retry（CPL-014/CPL-015）。
- CLI 增加 `workspace-scan` 与 `index-stage`；staging 报告显式返回 `published=false` 和 `CG-005`，不向 MCP 暴露未发布数据。
- 增加 Git branch/HEAD snapshot 身份、真实 branch switch 强制重建与跨分支 staging 隔离回归（CPL-013）。
- 增加 staging completion marker 写入顺序、missing/corrupt marker 重开检查、checkpoint-budget 安全 retry 和 `couplet.staging_inspection.v1`（CPL-015）。
- 增加 `c1-capacity`、`couplet.c1_capacity_evidence.v1` 与固定双语言 Medium/Large 语料；真实结果如实记录首次/增量、query、reopen、RSS、allocation 和数据库增长，当前 Performance/Capacity gate 为 FAIL。
- 增加 Codex/Claude Code 对三个 C1 MCP 工具的端到端 golden gate，证明未发布 staging 不进入 `workspace_status`、`code_search` 或 `symbol_get`。

### Changed

- 默认依赖从临时 SonnetDB 源码引用切换为官方 `SonnetDB.Core 3.1.0` 固定 package 和 content hash；Couplet 可独立 checkout 构建，但产品发布仍受 C1-C4 功能门禁约束。
- C1 能力门控从“未实现”细化为“staging 已实现、generation 发布受阻”；exact/fulltext MCP 继续返回稳定 `capability_unavailable`，原因码为 `generation_publish_blocked`。
- Native AOT 的 `index-stage` 通过固定包公开 options 关闭不兼容的 background flush/compaction/retention/KV maintenance worker，并在 staging/handshake 中显式报告 CG-006；JIT 路径继续启用默认后台维护。

### Fixed

- SonnetDB Document 批次累计超过 checkpoint budget 时，只对固定包保证“WAL append 前拒绝”的稳定异常执行 checkpoint 后原批次 retry，避免中大型 staging 因 admission budget 提前失败。
