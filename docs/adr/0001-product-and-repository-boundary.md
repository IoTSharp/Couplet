# ADR 0001：产品与仓库边界

- 状态：Accepted
- 日期：2026-08-10

## 背景

代码知识产品需要快速迭代语言适配、Agent 协议、embedding、评测和分发；SonnetDB 则必须保持通用多模型数据库边界、Core 稳定性和独立发布。把两者放在同一仓库或让 SonnetDB 了解代码领域，会混合发布节奏和责任，也容易诱发为产品需求复制数据库能力。

## 决策

1. 产品正式名称为 **Couplet**，canonical repository 为 `https://github.com/IoTSharp/Couplet`。
2. Couplet 与 SonnetDB 使用同级独立仓库、独立 issue/CI/version/release：本机建议布局为 `D:\source\Couplet` 与 `D:\source\SonnetDB`。
3. **不把 Couplet 加为 SonnetDB 的 Git submodule，也不把 SonnetDB 源码 vendoring/submodule 到 Couplet。**
4. 正式构建由 Couplet 固定引用已发布的 `SonnetDB.Core` package version，并在启动时执行版本/capability handshake。
5. 本地跨仓开发可以使用不提交的 composite solution，或通过显式 opt-in MSBuild property 把 package reference 替换为同级 `ProjectReference`。默认 checkout/build 不依赖另一个仓库恰好存在。
6. 依赖方向永远是 `Couplet -> SonnetDB.Core`。SonnetDB 不引用 Couplet，也不包含代码 schema、语言 parser、MCP 或 Agent 接入。

## 责任

Couplet 负责 workspace/Git、语言语义、代码模型、增量协调、embedding provider、检索意图、context pack、MCP/CLI、Agent eval 和产品分发。

SonnetDB 负责通用 KV/Document/FullText/Vector/Native Graph/Hybrid Search、事务/快照/WAL/checkpoint/backup/recovery、执行计划、资源预算、容量和性能。

## 结果

- 两个项目可以独立发布和回滚，Couplet 的 package lock 明确记录所需 SonnetDB 版本。
- SonnetDB 的通用缺口必须在 SonnetDB 修复，Couplet 不通过 fork、复制内部代码或 submodule patch 绕开。
- 跨仓 breaking change 通过 package/API compatibility、capability handshake 和双方 CI 发现，而不是依赖一个父仓库同时移动两个 commit 指针。
- 本地联调需要额外的 opt-in 配置，但该成本小于 submodule 带来的所有权和发布耦合。

## 未决事项

仓库许可证由维护者另行决定；本 ADR 不授权复制 SonnetDB 的许可证或版权元数据。

## 实现状态

2026-08-25，CPL-007 已切换到包含公开 Graph API 的固定 `SonnetDB.Core 3.1.0` package 和 lock file，默认构建不再依赖相邻源码仓库。本 ADR 第 4 条已恢复为唯一正式构建方式；本地跨仓联调仍只能使用不提交的显式 opt-in 配置。
