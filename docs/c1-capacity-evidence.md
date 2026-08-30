# C1 Medium/Large 容量证据

## 结论

2026-08-25 在 `d0531e0` 基线上的任务 working tree 完成了真实 Medium 与 Large staging characterization。两档语料均达到固定 LOC、符号和双 adapter family 数量，首次/100 文件变化、exact、FullText、checkpoint/reopen 均实际执行；报告始终为 `published=false`，不读取或宣称 active generation。本文保留该次固定输入与原始结论，不把后续 source-lane 小型功能回归改写成当时已取得的容量证据。

C1 Performance/Capacity gate 为 **FAIL**。Medium 首次 staging 达到 5 分钟目标，Large 首次 staging 超过 45 分钟；两档的 100 文件路径都重写完整 generation，远超增量目标；Medium/Large peak working set 均超过资源目标。exact 与 FullText staging 探针达到延迟目标，但它们不是公开 MCP 查询，固定 package 也不返回 FullText candidates/examined 诊断，因此不能关闭 C1 gate。

## 固定输入与环境

- manifest：`fixtures/c1/capacity-manifest.v1.json`
- manifest SHA-256：`38f906b9b65f88e11bb2953fa2ee45e97105815c13d6b8a364230da1ee9fb1b4`
- generator：`couplet-c1-capacity-v1`，每档 50% C#、50% TypeScript，均为 lexical `Partial`
- CPU：Intel Core Ultra 9 185H，16 physical / 22 logical cores
- memory：63.45 GiB GC available memory
- storage：NVMe PC SN8000S WD 2048GB，ReFS
- OS/runtime：Windows 11 build 26200，.NET 10.0.11，JIT，High performance power plan
- background：interactive desktop，无主动竞争 workload

容量输出和 SonnetDB 数据库位于被 Git ignore 的 `artifacts/`，不提交语料、数据库或原始 benchmark artifact。机器报告使用 source-generated `couplet.c1_capacity_evidence.v1`，schema 位于 `contracts/indexing/v1/schema.json`。

## 结果

| 指标 | Medium | Large | 门禁解释 |
|---|---:|---:|---|
| corpus | 1m LOC / 1,000 files / 100k symbols | 10m LOC / 10,000 files / 1m symbols | 数量 PASS |
| corpus SHA-256 | `1b08f82ce0d2080384176109504dc186dedcbd6e5d0ad3f7187b076662d94272` | `44399732cf8f386130c3f08251bfcf1c871dd5eab7b0ae3063ca595eb8f42a40` | 可复现身份 |
| generation records | 1,000 files / 100k symbols / 100k chunks / 201k FullText | 10,000 files / 1m symbols / 1m chunks / 2.01m FullText | count/reopen PASS |
| cold initial total | 74.814 s | 3,261.419 s / 54.357 min | Medium <= 5 min；Large > 45 min FAIL |
| 100-file total | 73.086 s | 6,472.504 s / 107.875 min | > 3 s / 10 s，FAIL |
| exact warm P50/P95/P99, 30 samples | 0.181 / 0.230 / 7.101 ms | 0.298 / 11.043 / 22.730 ms | staging path latency PASS |
| FullText top-20 warm P50/P95/P99, 30 samples | 7.693 / 18.987 / 27.321 ms | 98.152 / 324.938 / 346.937 ms | staging path latency PASS；诊断不完整 |
| cold reopen + consistency | 14.337 s | 230.134 s | 单样本，未形成 recovery percentile |
| peak working set | 4.648 GiB | 28.329 GiB | > 4 GiB / 12 GiB，FAIL |
| managed allocations, initial total | 30.170 GiB | 832.264 GiB | 容量放大证据 |
| managed allocations, 100-file total | 36.425 GiB | 895.523 GiB | 全代重写放大 |
| final two-generation database | 3.087 GiB | 31.142 GiB | CG-005 下不能 lease-aware cleanup |

initial、100-file 和 reopen 各只有 1 个昂贵样本；报告中的 P50/P95/P99 因此数值相同，只是 schema 统一表示，不构成统计分位数。这个样本数不足本身保持 gate FAIL。exact 与 FullText 各 30 个 warm 样本；actual access path 分别为 `document_path_index:by_stable_id` 与 `document_fulltext:code_search`。exact 能报告 candidates/examined/returned = 1/1/1；FullText 固定 package 只能报告 returned=20，不能报告 candidates/examined。

## 恢复与发布边界

- 每次重建前先持久化删除旧 staging completion marker，再替换 collection；Document checkpoint 完成后才写 manifest。
- `InspectStaging` 在当前进程和 reopen 后校验 manifest identity/state、四个 path indexes、FullText index、Document/FullText count 和 index consistency。
- missing/corrupt completion marker 均拒绝为 incomplete；同 generation 可确定性 restage。
- 固定 package checkpoint budget 拒绝明确发生在 WAL append 前；Couplet 仅对这一稳定异常执行 checkpoint 后原批次 retry，其他 I/O 异常继续失败。
- 真实 Git branch switch 携带 branch/HEAD 到 snapshot，强制 `git_branch_changed` 全量重建；两个分支的 staging collection 在 reopen 后保持隔离。
- Codex 与 Claude Code 的真实 MCP initialize/tools-call 回归证明 `workspace_status`、`code_search`、`symbol_get` 均返回 `CG-005/generation_publish_blocked`，响应不包含 staging items。

这次 2026-08-25 容量运行没有覆盖 active publish 点 kill-before/after、query lease、cursor continuity 或 retired cleanup。后续 source-lane 小型回归已覆盖同进程 active/retired cursor lease、cutoff-aware cleanup，以及真实 `Couplet.Cli` 子进程在 commit 前后强杀/重开的原子可见性；这些结果没有在本文的 Medium/Large 固定语料上复测，也不提供进程重启 cursor、随机进程故障、双客户端、容量或长稳证据。因此 CG-005 继续为 `verifying`，C1 Performance/Capacity gate 继续为 **FAIL**；默认 package 的 Native AOT background maintenance 仍由 CG-006 阻塞。

## 复现命令

```powershell
dotnet run --project src/Couplet.Cli --configuration Release --no-build -- c1-capacity `
  --repository . `
  --scale medium `
  --workspace artifacts/c1-capacity/medium-v1/workspace `
  --database artifacts/c1-capacity/medium-v1/database `
  --report artifacts/c1-capacity/medium-v1/report.json `
  --commit working_tree_at_d0531e0 `
  --query-samples 30
```

把 `medium` 和目录名替换为 `large` 可执行 Large。固定硬件报告必须设置 `COUPLET_PHYSICAL_CORES`、`COUPLET_STORAGE_MODEL`、`COUPLET_POWER_PROFILE` 和 `COUPLET_BACKGROUND_LOAD`；缺失时机器报告会增加 `capacity_environment_incomplete`。
