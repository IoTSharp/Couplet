# ADR 0004：.NET 宿主与临时源码依赖

- 状态：Accepted
- 日期：2026-08-24
- 对应交付：CPL-007

## 背景

CPL-007 需要建立 .NET 10 solution、共享 application/core 边界、CLI/daemon/MCP Server executable，以及 `Couplet -> SonnetDB.Core` 的单向依赖。NuGet 上最新稳定 `SonnetDB.Core 3.0.1` 只包含 `net10.0` 资产，程序集声明 `IsTrimmable=True` 与 `IsAotCompatible=True`，但没有公开 `SonnetDB.Graphs` 或 `GraphStore` 类型，不能作为后续原生图联调基线。

当前 canonical SonnetDB 仓库提交 `a0fefe15c4ea4d3a5f2a4a2c4f69d6930b9c6c70` 已包含公开 `SonnetDB.Graphs.GraphStore`。在新 package 发布前，Couplet 必须能够编译当前公开 API，同时不能复制源码、创建第二个 SonnetDB checkout、读取内部格式或把源码引用误写成可发布状态。

## 决策

1. solution 分为 `Couplet.Core`、`Couplet.Application`、`Couplet.Infrastructure.SonnetDb`、`Couplet.Cli`、`Couplet.Daemon` 和 `Couplet.McpServer`；只有 adapter 引用 SonnetDB，executables 只组合 adapter。
2. 当前使用相邻 canonical SonnetDB 仓库的 commit-pinned `ProjectReference`，固定提交为 `a0fefe15c4ea4d3a5f2a4a2c4f69d6930b9c6c70`。构建在解析引用前校验提交、Core 相关源码工作树和两个 runtime package 版本；SonnetDB 其他子系统的并发修改不进入 Couplet 输出。
3. NuGet restore graph 暂时排除外部项目，再以受控 MSBuild restore/build 把 SonnetDB 的 `obj/bin` 和 Jieba 字典生成输出重定向到 Couplet 已忽略的 `artifacts/sonnetdb`，避免修改 SonnetDB 工作树。
4. SonnetDB 源码当前两个运行时包 `System.IO.Hashing` 与 `System.Numerics.Tensors` 在 Couplet adapter 中显式固定为 `10.0.10`，防止隔离 restore 后 framework-dependent 输出缺失运行时闭包。
5. 所有 Couplet 生产项目启用 trim/AOT analyzer，并关闭反射型 `System.Text.Json`。三个 executable 可以用 `CoupletPublishAot=true` 生成 Native AOT，但当前结果只覆盖诊断/生命周期骨架。
6. 不创建 parser worker；typed MCP executable 存在，但 `serve` 必须返回 `capability_unavailable`，不得注册或模拟尚未交付的工具。

## 固定 package 迁移门禁

SonnetDB 发布包含当前 Graph public API 的新 `SonnetDB.Core` 后，必须在同一依赖迁移交付中：

1. 把 adapter 改为固定 `PackageReference`，版本只在集中版本文件出现一次，并重新生成 lock files。
2. 删除源码提交校验、外部 restore 隔离和源码构建输出重定向。
3. 运行相同的 public Graph API 编译测试、capability/version 报告测试、Release build 和逐 executable trim/AOT publish。
4. 验证 package 的依赖许可证、resolved graph、程序集 commit/version 与新发布说明一致。
5. 在迁移完成前，不发布 Couplet 二进制或 NuGet 包，也不允许 CI 跟随 SonnetDB `main`。

## 结果

- Couplet 当前能针对真实 Graph public API 建立单向编译边界，且 SonnetDB 源码不被复制或修改。
- 默认本地构建依赖固定位置的相邻仓库，CI 依赖固定提交的独立 checkout；因此 Couplet 尚不能作为独立源码包发布。
- 当前 AOT PASS 不覆盖未来 parser、MCP SDK、workspace/indexer 或 provider 依赖；引入这些依赖时必须更新 CPL-007 矩阵。
