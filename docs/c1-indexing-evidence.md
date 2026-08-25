# C1 增量索引实现与证据

## 当前结论

C1 处于实现中，不是完成状态。CPL-010~012 已有可运行实现；CPL-013~015 已完成 snapshot/增量计划、真实 Git branch/HEAD switch 重建、SonnetDB Document/FullText staging、completion marker 恢复检查、checkpoint-budget 安全 retry 和 reopen 校验部分。固定 `SonnetDB.Core 3.1.0` 尚未提供 Couplet 所需的跨模型 active generation publication、query snapshot lease、cursor continuity 与安全 retired generation cleanup public contract，因此 `CG-005` 保持 active，三个 C1 MCP 工具继续 unavailable。Native AOT 默认 KV worker shutdown 另受 `CG-006` 阻塞。

2026-08-25 的真实 Medium/Large characterization 已完成，但 Correctness/Recovery 与 Performance/Capacity 两个独立门禁均为 **FAIL**。这些数据是缺口和优化输入，不是 C1 容量声明或完成证据。

机器可读合同位于 [`contracts/indexing/v1/schema.json`](../contracts/indexing/v1/schema.json)，覆盖 workspace discovery、UTF-8 source span、parsed snapshot、incremental plan、generation manifest 和 staging report。生产 JSON 全部通过 source-generated `CoupletJsonContext`；没有反射 serializer fallback。

## 已实现路径

| 交付 | 当前证据 | 公开能力 |
|---|---|---|
| CPL-010 | canonical path；Git HEAD/branch/remote/worktree；tracked/untracked/ignored；deny > ignore；symlink/binary/generated/large/unreadable 分类；bounded watcher overflow 转 full rescan | `workspace-scan` 可用 |
| CPL-011 | replaceable `ILanguageAdapter`；C#、TypeScript/JavaScript lexical adapter 固定为 `Partial`；unsupported/large file 为 `TextOnly` | adapter 能力可诊断，不宣称完整语义 |
| CPL-012 | deterministic file/symbol/chunk IDs；qualified identity/signature；UTF-8 byte/line/column span；content hash、provenance、adapter version、confidence | staging records 可追溯 |
| CPL-013 | frozen snapshot；added/modified/deleted/content-hash rename；producer upgrade full rebuild；真实 Git branch/HEAD switch 强制重建与跨分支 staging 隔离；debounced watcher | 原子 publish 与 branch-switch query visibility 阻塞 |
| CPL-014 | generation collection；unique stable ID 与 path/qualified identity indexes；FullText；512-record batch；actual path probes | `index-stage` 可用；MCP exact/fulltext 不可用 |
| CPL-015 | parse failure reason；cancellation；same-generation deterministic rebuild；completion marker 先失效后替换；missing/corrupt marker 拒绝；index/count consistency；checkpoint/reopen inspection | publish crash/cursor/lease/cleanup 阻塞 |

## SonnetDB 接线

每个 staging generation 写入独立 Document collection。文件、符号和 chunk 使用同一 source-generated JSON record contract；`$.stable_id` 为唯一 path index，`$.record_type`、`$.path`、`$.qualified_identity` 为附加 path indexes，`$.search_text` 写入 `code_search` FullText index。

写入前先删除并持久化旧 completion marker，再替换 generation collection；Document checkpoint 完成后才写入 `GenerationState.Staging` manifest。`InspectStaging` 同时校验 manifest identity/state、Document count、FullText document count、四个 path/FullText index 和 Document index consistency，missing/corrupt marker 均被拒绝。内部探针冻结实际访问路径 `document_path_index:by_stable_id` 和 `document_fulltext:code_search`。

这条路径不建立 active pointer，不发 query lease，也不删除旧 generation。CLI 报告固定包含 `published=false` 和 `blocking_gap=CG-005`，MCP 的 `workspace_status`、`code_search`、`symbol_get` 返回 `capability_unavailable` / `generation_publish_blocked`。Native AOT 下使用公开 options 禁用 background flush/compaction/retention/KV maintenance 以避免固定包不支持的 `Thread.Interrupt()` shutdown，并返回 `limitations=["CG-006:sonnetdb_background_maintenance_disabled"]`；JIT 路径继续启用默认维护。

## 验证命令

```powershell
dotnet restore Couplet.slnx --locked-mode
dotnet build Couplet.slnx --configuration Release --no-restore
dotnet test tests/Couplet.Tests/Couplet.Tests.csproj --configuration Release --no-build --no-restore
./eng/verify-roadmap.ps1
dotnet run --project src/Couplet.Cli --configuration Release --no-build -- workspace-scan --workspace .
dotnet run --project src/Couplet.Cli --configuration Release --no-build -- index-stage --workspace . --database artifacts/c1-smoke-db
dotnet publish src/Couplet.Cli/Couplet.Cli.csproj -c Release -r win-x64 -p:CoupletPublishAot=true
dotnet publish src/Couplet.Daemon/Couplet.Daemon.csproj -c Release -r win-x64 -p:CoupletPublishAot=true
dotnet publish src/Couplet.McpServer/Couplet.McpServer.csproj -c Release -r win-x64 -p:CoupletPublishAot=true
```

自动化覆盖 clean/dirty Git revision、嵌套构建产物、deny/ignore、path/symlink escape、Git 子进程取消、发现后文件突变、C#/TypeScript stable symbol 与 UTF-8 span、rename/modify/delete、producer upgrade、watch debounce、真实 Git branch switch，以及 SonnetDB staging/retry/completion-marker/reopen/access path。Codex 与 Claude Code 双客户端通过真实 MCP `initialize` + `tools/call` 回归验证 `workspace_status`、`code_search`、`symbol_get` 均返回 `CG-005/generation_publish_blocked`，且响应不含 staging items。

2026-08-25 在 `329519d` C0 基线、`d0531e0` C1 staging 提交上的首次仓库 smoke：对 Couplet 自身执行 `workspace-scan` 得到 115 个候选、113 个 included files，随后 `index-stage` 成功写入 113 files、508 logical symbols、584 chunks 和 1205 FullText documents，`problems=0`。本次收口后 Release build 为 0 warning/0 error，56/56 tests PASS；CLI、daemon 和 MCP Server 的 win-x64 Native AOT publish 均为 0 个未处置 IL/AOT warning，三个发布后的 executable 均能启动并报告固定 `SonnetDB.Core 3.1.0`。当前 working tree 的 AOT CLI staging 写入 117 files、540 logical symbols、620 chunks 和 1277 FullText documents，`problems=0`，同时按合同保持 `published=false`、`blocking_gap=CG-005` 并显式报告 CG-006 后台维护 limitation。烟测数据库在验证后删除，未作为仓库 artifact 保留。

Medium/Large 容量运行使用固定 manifest `38f906b9b65f88e11bb2953fa2ee45e97105815c13d6b8a364230da1ee9fb1b4`。Medium（1m LOC / 100k symbols）initial 74.814 s，100-file 73.086 s，peak RSS 4.648 GiB；Large（10m LOC / 1m symbols）initial 3,261.419 s，100-file 6,472.504 s，peak RSS 28.329 GiB。Large initial、两档增量和两档内存均未达目标；详细语料 hash、P50/P95/P99、allocation、reopen 与数据库放大见 [C1 Medium/Large 容量证据](c1-capacity-evidence.md)。

## 未通过门禁

- Correctness/Recovery：**FAIL**。staging marker 损坏/缺失和 branch-switch 隔离已验证，但仍缺 active generation 原子切换、query lease、kill-before/after-publish、cursor continuity、branch-switch 公开查询可见性和 lease-aware cleanup。
- Performance/Capacity：**FAIL**。Medium/Large 已有固定语料实测；Large initial、两档 100-file 路径和两档 peak RSS 未达门禁，initial/incremental/reopen 昂贵路径只有单样本，I/O 与 FullText candidates/examined 诊断也不完整。
- Native AOT lifecycle：固定包默认 compaction/retention/KV worker shutdown 不兼容 AOT；当前 staging 关闭相关 background workers，CG-006 关闭前不满足长期维护或容量门禁。
- 双客户端合同：Codex/Claude Code 的能力门控已验证，但 `workspace_status`、`code_search`、`symbol_get` 在 generation 未发布时依合同不能返回索引数据。

关闭 C1 需要先由 SonnetDB 提供并验证 `CG-005` 所列 public contract，并修复或正式支持 `CG-006` 的 AOT-safe KV maintenance lifecycle，再在 Couplet 完成 crash/recovery、MCP cursor/lease、删除/重命名/branch switch 和固定硬件容量回归。不得以应用层第二提交日志、直接查询 staging、吞 dispose 异常或隐藏 maintenance disable 绕过。
