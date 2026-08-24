# CPL-007 基础与发布边界

## 1. 当前范围

CPL-007 提供可构建、可测试的 .NET 10 solution 和最小诚实可执行面。它不实现 CPL-003 capability handshake、CPL-004 eval runner、CPL-006 typed MCP schema，也不实现 C1 索引、C2 图工具或任何 fallback。

当前可观察行为：

- CLI：`version`、`capabilities`。
- daemon：`version`、`capabilities`、可取消的 `run` 生命周期。
- MCP Server：`version`、`capabilities`；`serve` 返回稳定 `capability_unavailable` 和退出码 2。
- capability JSON 使用 `cpl-007.capabilities.v1`，生产序列化只使用 source-generated `JsonSerializerContext`。

## 2. solution 与依赖图

| 项目 | 职责 | 允许依赖 |
|---|---|---|
| `Couplet.Core` | capability/lifecycle/error DTO 与组件类型 | BCL |
| `Couplet.Application` | 报告编排、命令和生命周期、source-generated JSON | `Couplet.Core` |
| `Couplet.Infrastructure.SonnetDb` | SonnetDB 版本/Graph API 探针与 composition root | Application、Core、固定 SonnetDB 源码与其两个运行时包 |
| `Couplet.Cli` | 命令行进程边界 | SonnetDB adapter |
| `Couplet.Daemon` | 可取消的本地长运行进程边界 | SonnetDB adapter |
| `Couplet.McpServer` | 未来 typed MCP 独立进程边界 | SonnetDB adapter |
| `Couplet.Tests` | CPL-007 自动化合同 | 上述项目与 test-only packages |

SonnetDB 不引用 Couplet。当前 adapter 只读取公开 `GraphStore` 类型和程序集元数据，不打开数据库、不探测内部文件、不宣称 capability ready。

## 3. SonnetDB 固定策略

### 当前源码模式

- 模式：`source_project_reference`。
- canonical local root：`D:\source\SonnetDB`，可通过显式 MSBuild `SonnetDbSourceRoot` 覆盖 CI 路径。
- 固定提交：`a0fefe15c4ea4d3a5f2a4a2c4f69d6930b9c6c70`。
- 输入验证：`src/SonnetDB.Core` 与 `Directory.Build.props` 必须相对固定提交保持干净，两个 runtime package 必须仍为 `10.0.10`。
- 版本报告：当前程序集为 `0.0.0-dev+a0fefe15...`。
- 构建隔离：外部 restore/build 和 Jieba 字典目标输出写入 `artifacts/sonnetdb`；普通 Couplet restore/build 已验证不会刷新 SonnetDB 的 `obj` 或已提交字典。
- 锁文件：普通 build/CI 使用提交的 `packages.lock.json`；AOT restore 使用各项目 `obj/packages.aot.<RID>.lock.json`，不改写普通锁。

### 已拒绝的发布包

`SonnetDB.Core 3.0.1` 发布于 2026-07-08，NuGet metadata 为 MIT，依赖 `System.IO.Hashing 10.0.9` 与 `System.Numerics.Tensors 10.0.9`，程序集声明 trim/AOT compatible，但公开类型中没有 `SonnetDB.Graphs`。因此该包只作为 spike 证据，不进入 Couplet dependency graph。

新 package 发布后的迁移步骤由 [ADR 0004](adr/0004-dotnet-host-and-source-dependency.md) 冻结；当前源码模式阻塞任何 Couplet 独立发布。

## 4. 依赖与许可证

Couplet 仓库自身尚未决定许可证；下表只记录依赖的上游许可证，不给 Couplet 增加许可证元数据。

| 范围 | 依赖 | 固定版本/提交 | 上游许可证 | trim/AOT 风险 |
|---|---|---|---|---|
| runtime | SonnetDB Core source | `a0fefe15...` | MIT（SonnetDB build metadata） | `IsTrimmable`/`IsAotCompatible` 声明为 true；当前宿主 AOT publish 通过，但真实数据库路径尚未由 Couplet 调用 |
| runtime | `System.IO.Hashing` | `10.0.10` | MIT | BCL companion package；三个宿主 AOT publish 无 warning |
| runtime | `System.Numerics.Tensors` | `10.0.10` | MIT | BCL companion package；三个宿主 AOT publish 无 warning |
| test only | `Microsoft.NET.Test.Sdk` | `18.8.1` | MIT | 不进入生产输出 |
| test only | `xunit` | `2.9.3` | Apache-2.0 | 不进入生产输出 |
| test only | `xunit.runner.visualstudio` | `3.1.5` | Apache-2.0 | `PrivateAssets=all`，不进入生产输出 |

更新策略：runtime 版本只随固定 SonnetDB source commit 或正式 package 迁移一起更新；test-only package 通过单独 build/test 维护变更更新。任何新增 runtime dependency 都必须补许可证、trim/AOT 与不可用行为。

## 5. trim/AOT spike

2026-08-24 在 Windows win-x64、.NET SDK `10.0.400` 上取得以下结果：

| 验证 | 结果 | 边界 |
|---|---|---|
| Release solution build | PASS，0 warning / 0 error | Couplet 自有项目和固定 SonnetDB Core source |
| tests | PASS，8/8 | 启动、版本/能力、取消/关闭、JSON、依赖与 analyzer policy |
| CLI Native AOT publish/run | PASS，0 IL/AOT warning | 仅 `version` / `capabilities` |
| daemon Native AOT publish/run | PASS，0 IL/AOT warning | 能力报告；托管测试覆盖取消后 `started -> stopped` |
| MCP Server Native AOT publish/run | PASS，0 IL/AOT warning | `serve` 仍返回 `capability_unavailable` |
| reflection JSON disabled | PASS | `JsonSerializerIsReflectionEnabledByDefault=false`，报告只走 generated `JsonTypeInfo` |
| SonnetDB workspace isolation | PASS | 受控 restore 后 SonnetDB `obj` hash/timestamp 不变，Git 工作树干净 |

未验证：Linux/macOS Native AOT、真实数据库 open/query、Graph workload、未来 MCP SDK、语言 parser worker、安装包和长稳。它们不能从本次 PASS 外推。

## 6. executable/worker 发布矩阵

| 单元 | 当前功能 | 普通 Release | Native AOT win-x64 | 可独立发布 | 主要限制 |
|---|---|---|---|---|---|
| `Couplet.Cli` | 版本与能力报告 | PASS | PASS | 否 | 未实现 start/stop/index/rebuild/diagnostics/eval |
| `Couplet.Daemon` | 版本/能力与可取消空生命周期 | PASS | PASS | 否 | 未打开 workspace/SonnetDB，不是可用 daemon 产品 |
| `Couplet.McpServer` | 版本/能力；拒绝 `serve` | PASS | PASS | 否 | 无 MCP transport/schema/tool registration |
| parser worker | 未创建 | N/A | N/A | 否 | parser 选择与隔离属于后续交付，不能预判 AOT |

三个 executable 的 AOT 命令统一为：

```powershell
dotnet publish <project.csproj> --configuration Release --runtime win-x64 -p:CoupletPublishAot=true
```

源码引用、固定 package 迁移、跨平台 publish 与相应功能 gate 全部关闭后，才能改变“可独立发布”列。
