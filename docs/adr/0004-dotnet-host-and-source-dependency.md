# ADR 0004：.NET 宿主与 SonnetDB 固定依赖

- 状态：Accepted（2026-08-25 修订）
- 初始日期：2026-08-24
- 对应交付：CPL-007

## 背景

CPL-007 需要建立 .NET 10 solution、共享 application/core 边界、CLI/daemon/MCP Server executable，以及 `Couplet -> SonnetDB.Core` 的单向依赖。2026-08-24 当时已发布的 `SonnetDB.Core 3.0.1` 没有公开 `SonnetDB.Graphs.GraphStore`，因此初始决策临时使用 commit-pinned 相邻源码引用，并明确阻塞独立发布。

2026-08-25，官方 `SonnetDB.Core 3.1.0` 已包含公开 Graph API、`IsTrimmable=True` 和 `IsAotCompatible=True`。固定 package 迁移门禁已具备执行条件。

## 决策

1. solution 分为 `Couplet.Core`、`Couplet.Application`、`Couplet.Infrastructure.SonnetDb`、`Couplet.Cli`、`Couplet.Daemon` 和 `Couplet.McpServer`；只有 adapter 引用 SonnetDB，executables 只组合 adapter。
2. 默认构建固定 `SonnetDB.Core 3.1.0` `PackageReference`，版本只在 `Directory.Packages.props` 出现；每个消费项目的 lock file 固定官方 package content hash。restore 使用已忽略的仓库本地 `artifacts/nuget` cache，避免同 ID/version 的本机开发包污染官方依赖解析。
3. 删除相邻源码提交校验、额外 SonnetDB checkout、外部 restore 和构建输出重定向。默认 checkout/build 不依赖 `D:\source\SonnetDB`。
4. `System.IO.Hashing 10.0.10` 与 `System.Numerics.Tensors 10.0.10` 作为 package 的传递依赖进入 resolved graph，不在 Couplet adapter 重复直接引用。
5. capability handshake 报告 package/assembly version、informational commit、trim/AOT 元数据、public API 联调状态、release level 和阻塞 gap。存在 public type 不等于 Couplet 可以宣称 Preview/Beta/Production。
6. 所有 Couplet 生产项目启用 trim/AOT analyzer，并关闭反射型 `System.Text.Json`；声明 AOT 的 executable 必须实际 publish/run 且保持 0 个 IL/AOT warning。
7. MCP Server 可以提供真实只读 stdio 协议和 typed schema，但 C1-C3 工具在能力未就绪时必须返回结构化 unavailable 错误，不建立 fallback。

## 迁移验证

- 官方 package 的 id/version/content hash/informational commit 由 `contracts/c0-handshake.v1.json`、lock file 和自动化测试共同锁定。
- 依赖图测试保证只有 `Couplet.Infrastructure.SonnetDb` 直接引用 `SonnetDB.Core`，SonnetDB 不引用 Couplet。
- Release build、35 个测试、CLI/MCP smoke 和三个 win-x64 Native AOT publish/run 是迁移证据。
- 本机曾存在同版本的本地 package cache；C0 使用隔离的官方源缓存生成 lock file，不修改用户全局 NuGet cache。CI 只按 lock file 使用官方源内容。

## 结果

- Couplet 已恢复为可独立 checkout/restore/build 的默认模式，初始临时源码决策终止。
- 固定 package 迁移本身不表示索引、Graph Preview 或独立产品发布 gate 已通过；CG-001、CG-002 和 CG-005 继续阻塞对应阶段。
- 当前 AOT PASS 覆盖 C0 DTO、schema/evidence runner、stdio MCP 和 capability handshake，不覆盖未来 parser、真实数据库/indexer、provider 或安装包；引入这些边界时必须更新 CPL-007 矩阵。
