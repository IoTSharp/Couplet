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

### Changed

- 默认依赖从临时 SonnetDB 源码引用切换为官方 `SonnetDB.Core 3.1.0` 固定 package 和 content hash；Couplet 可独立 checkout 构建，但产品发布仍受 C1-C4 功能门禁约束。

### Fixed

- 暂无。
