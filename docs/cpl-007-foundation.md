# CPL-007 基础与发布边界

## 1. 当前范围

CPL-007 提供可独立 restore/build/test 的 .NET 10 solution、固定 SonnetDB package、三个 executable、source-generated JSON 和诚实能力报告。C0 同时完成 capability handshake、evidence runner 和 typed MCP schema；C1 索引、C2 图查询和 C3 混合检索仍未实现。

当前可观察行为：

- CLI：`version`、`capabilities`、`c0-evidence`、`fixture-generate`。
- daemon：`version`、`capabilities`、可取消的 `run` 生命周期。
- MCP Server：`version`、`capabilities`，以及显式 `--workspace` 绑定的 stdio `initialize`、`ping`、`tools/list`、`tools/call`。
- MCP Server 公开八个只读 schema；未就绪工具返回版本化错误和 gap，不执行查询或 fallback。
- 所有生产 JSON 使用 source-generated `JsonSerializerContext`，未知请求字段拒绝，反射 JSON 默认关闭。

## 2. solution 与依赖图

| 项目 | 职责 | 允许依赖 |
|---|---|---|
| `Couplet.Core` | graph/generation/security/MCP/eval/capability DTO 与稳定算法 | BCL |
| `Couplet.Application` | schema、协议校验/host、evidence/fixture runner、命令和生命周期 | `Couplet.Core` |
| `Couplet.Infrastructure.SonnetDb` | SonnetDB 固定 package capability probe 与 composition root | Application、Core、`SonnetDB.Core 3.1.0` |
| `Couplet.Cli` | 命令行进程边界 | SonnetDB adapter |
| `Couplet.Daemon` | 可取消的本地长运行进程边界 | SonnetDB adapter |
| `Couplet.McpServer` | typed read-only MCP stdio 进程边界 | SonnetDB adapter |
| `Couplet.Tests` | C0 schema、兼容、安全、异常和 runner 自动化 | 上述项目与 test-only packages |

SonnetDB 不引用 Couplet。adapter 只使用公开类型和程序集元数据，不读取内部 key/file/WAL 格式，也不因 API 存在而把产品 capability 标为可用。

## 3. SonnetDB 固定策略

- 模式：`fixed_package`。
- package：`SonnetDB.Core 3.1.0`。
- 官方 package content hash 和 informational commit：见 [`contracts/c0-handshake.v1.json`](../contracts/c0-handshake.v1.json)。
- restore cache：仓库内已忽略的 `artifacts/nuget`；不读取或修改用户全局 NuGet cache。
- public API：KV snapshot、Document、FullText、Vector、Native Graph、path budgets 和 diagnostics 可用于后续联调；release level 继续为 unavailable。
- 未满足能力：跨模型 generation 原子发布和公开 shared typed hybrid plan，分别由 CG-005、CG-002 阻塞。
- 旧 `3.0.1` package 和临时 source `ProjectReference` 均已退出 dependency graph。

## 4. 依赖与许可证

Couplet 仓库自身尚未决定许可证；下表只记录依赖的上游许可证，不给 Couplet 增加许可证元数据。

| 范围 | 依赖 | 固定版本 | 上游许可证 | trim/AOT 风险 |
|---|---|---|---|---|
| runtime | `SonnetDB.Core` | `3.1.0` | MIT | 声明 trim/AOT compatible；C0 宿主 publish 通过，真实数据库/indexer 路径未验证 |
| transitive runtime | `System.IO.Hashing` | `10.0.10` | MIT | 由 SonnetDB package 引入 |
| transitive runtime | `System.Numerics.Tensors` | `10.0.10` | MIT | 由 SonnetDB package 引入 |
| test only | `Microsoft.NET.Test.Sdk` | `18.8.1` | MIT | 不进入生产输出 |
| test only | `xunit` | `2.9.3` | Apache-2.0 | 不进入生产输出 |
| test only | `xunit.runner.visualstudio` | `3.1.5` | Apache-2.0 | `PrivateAssets=all` |

runtime package 只通过单独兼容性变更升级，必须同步 lock、handshake、许可证、trim/AOT 和 unavailable 行为证据。

## 5. trim/AOT 验证

2026-08-25 在 Windows win-x64、.NET SDK `10.0.400` / runtime `10.0.11` 上验证：

| 验证 | 结果 | 边界 |
|---|---|---|
| Release solution build | PASS，0 warning / 0 error | Couplet C0 与官方 SonnetDB package |
| tests | PASS，35/35 | stable ID/generation/security/schema/双客户端/错误/cursor/runner/依赖 |
| CLI Native AOT publish/run | PASS，0 IL/AOT warning | 版本、能力和 C0 evidence |
| daemon Native AOT publish/run | PASS，0 IL/AOT warning | 能力报告与可取消生命周期 |
| MCP Server Native AOT publish/run | PASS，0 IL/AOT warning | initialize、schema discovery 和 unavailable tool call |
| reflection JSON disabled | PASS | typed DTO 全部绑定 generated `JsonTypeInfo`，协议包装使用 `Utf8JsonWriter` |

未验证：Linux/macOS Native AOT、真实数据库 open/query、语言 parser worker、安装包和长稳。它们不能从本次 PASS 外推。

## 6. executable/worker 发布矩阵

| 单元 | 当前功能 | 普通 Release | Native AOT win-x64 | 可独立发布 | 主要限制 |
|---|---|---|---|---|---|
| `Couplet.Cli` | 版本/能力、fixture/evidence runner | PASS | PASS | 否 | 未实现 start/stop/index/rebuild/diagnostics |
| `Couplet.Daemon` | 版本/能力与可取消空生命周期 | PASS | PASS | 否 | 未打开 workspace/SonnetDB，不是可用索引 daemon |
| `Couplet.McpServer` | typed stdio/schema/unavailable tools | PASS | PASS | 否 | 无可查询 generation，八工具产品能力 unavailable |
| parser worker | 未创建 | N/A | N/A | 否 | parser 选择与隔离属于 C1 |

统一 AOT 命令：

```powershell
dotnet publish <project.csproj> --configuration Release --runtime win-x64 -p:CoupletPublishAot=true
```

“固定 package 可独立构建”不等于“可独立发布产品”。只有对应功能、跨平台、安装和阶段 gate 全部关闭后才能改变“可独立发布”列。
