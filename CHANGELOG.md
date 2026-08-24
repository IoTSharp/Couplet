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

### Changed

- `SonnetDB.Core 3.0.1` 尚无公开 Graph API；CPL-007 临时固定引用 SonnetDB 提交 `a0fefe15c4ea4d3a5f2a4a2c4f69d6930b9c6c70`，并在新包发布前阻塞 Couplet 独立发布。

### Fixed

- 暂无。
