# CPO 遥测 Schema（M1 定稿 v1.0）

> 状态：**定稿 v1.0**（M1）。本文件是遥测事件流的唯一事实来源（single source of truth）。
> 所有组件（采集、存储、回放、未来 AI 层）按本契约走。变更需更新本文件并记录到 DISCUSSIONS.md。
>
> 存储形态：SQLite 单表 `events`（时间序列 + JSON payload），表结构即本 schema 的落盘形态。
> 时间戳统一为 **Unix 毫秒（UTC）**，字段名 camelCase。

## 0. 通用约定

| 约定 | 值 |
|---|---|
| 时间戳 | Unix epoch 毫秒，UTC，字段名 `tsMs`，类型 INTEGER |
| 事件标识 | `id` INTEGER PRIMARY KEY AUTOINCREMENT（落盘分配） |
| 事件类型 | `type` TEXT，值见下表，落盘索引 `(ts_ms, type)` |
| 载荷 | `payload` TEXT（JSON），含事件全部业务字段 |
| 进程标识 | `pid` INT；`ppid` INT（父进程）；`name` TEXT（exe 名）；`path` TEXT（可执行文件完整路径，可空） |
| 采样频率 | 配置化（见 `SamplingConfig`），默认系统级 2s、进程级 2s，可运行时调整 |

事件类型枚举：

| `type` | 事件 | 产生方 |
|---|---|---|
| `process.lifecycle` | 进程启动/退出 | interop 采集 |
| `sample.cpu` | CPU 周期采样（进程级 + 系统级） | interop 采集 |
| `sample.memory` | 内存周期采样（进程级 + 系统级） | interop 采集 |
| `ui.foreground` | 前台窗口变化 | GUI（M2 接入，schema 已定） |
| `policy.decision` | 策略引擎每次决策 | 引擎（M2 接入，schema 已定） |
| `policy.action` | 动作执行与恢复 | 执行路径（M2 接入，schema 已定） |
| `rule.changed` | 规则变更 | 规则管理（M2 接入，schema 已定） |
| `policy.intervention_toggled` | ProBalance 开关切换 | 引擎控制面（M3 接入，schema 已定） |

---

## 1. process.lifecycle — 进程启动 / 退出

采集器轮询比对前后快照得出（ETW 订阅为 M2 增强项）。

| 字段 | 类型 | 单位 | 必填 | 说明 |
|---|---|---|---|---|
| `tsMs` | long | ms | ✅ | 事件时间 |
| `kind` | enum `started` \| `exited` | — | ✅ | 启动或退出 |
| `pid` | int | — | ✅ | 进程 ID |
| `ppid` | int | — | ✅ | 父进程 ID |
| `name` | string | — | ✅ | 进程名（如 `msedge.exe`） |
| `path` | string | — | ❌ | 完整路径，获取失败时 null |

## 2. sample.cpu — CPU 周期采样

每个采样周期产生 1 条系统级 + N 条进程级事件。

| 字段 | 类型 | 单位 | 必填 | 说明 |
|---|---|---|---|---|
| `tsMs` | long | ms | ✅ | 采样时刻 |
| `scope` | enum `system` \| `process` | — | ✅ | 采样范围 |
| `pid` | int | — | process 必填 | 进程 ID |
| `name` | string | — | process 必填 | 进程名 |
| `cpuPercent` | double | % | ✅ | 该间隔平均占用。进程级 0~100；系统级 0~100（整体占用率，非×核数） |
| `totalCpuMs` | long | ms | ❌ | 进程累计 CPU 时间（kernel+user），用于百分比计算与校验 |
| `coreCount` | int | — | system 必填 | 逻辑核心数 |
| `intervalMs` | long | ms | ✅ | 本次采样间隔（用于百分比还原） |

## 3. sample.memory — 内存周期采样

每个采样周期产生 1 条系统级 + N 条进程级事件。

| 字段 | 类型 | 单位 | 必填 | 说明 |
|---|---|---|---|---|
| `tsMs` | long | ms | ✅ | 采样时刻 |
| `scope` | enum `system` \| `process` | — | ✅ | 采样范围 |
| `pid` | int | — | process 必填 | 进程 ID |
| `name` | string | — | process 必填 | 进程名 |
| `workingSetBytes` | long | bytes | ❌ | 进程工作集 |
| `privateBytes` | long | bytes | ❌ | 进程私有内存 |
| `availableBytes` | long | bytes | system 必填 | 系统可用物理内存 |
| `totalBytes` | long | bytes | system 必填 | 系统总物理内存 |
| `commitChargePercent` | double | % | ❌ | 系统提交负载占比（可空） |

## 4. ui.foreground — 前台窗口变化

GUI 侧 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 事件驱动产生（见 SPEC §6 前台检测归属决策）。**已落地（M3 会话⑳）**：app 经 gRPC `ReportForeground` 上报，service 写入引擎前台输入并落盘本事件；`windowTitle` 留 null（隐私红线，见 §10）。

| 字段 | 类型 | 单位 | 必填 | 说明 |
|---|---|---|---|---|
| `tsMs` | long | ms | ✅ | 切换时刻 |
| `pid` | int | — | ✅ | 前台进程 ID |
| `name` | string | — | ✅ | 前台进程名 |
| `windowTitle` | string | — | ❌ | 窗口标题，可空 |

## 5. policy.decision — 策略引擎决策

引擎每次评估输出一条。输入快照 + 结论双 JSON（机器可读）。

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `tsMs` | long | ✅ | 决策时刻 |
| `trigger` | string | ✅ | 触发条件描述（如 `cpu.storm`） |
| `targetPid` | int | ✅ | 决策目标进程 |
| `targetName` | string | ✅ | 目标进程名 |
| `proposedActions` | string(JSON) | ✅ | 建议动作数组（`ProposalBus` 输出，含参数与理由） |
| `inputSnapshot` | string(JSON) | ✅ | 决策输入快照（负载/前台/规则状态，脱敏字段按 §7 隐私红线） |
| `mode` | enum `supervised` \| `automatic` | ✅ | 决策模式（监督=仅建议；自动=直接执行） |
| `conclusion` | string(JSON) | ✅ | 引擎结论（采用/否决 + 理由，机器可读 + 人类可读双视图） |

## 6. policy.action — 动作执行与恢复

执行路径（`ExecutionPath`）每次实际干预/恢复输出一条。

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `tsMs` | long | ✅ | 动作时刻 |
| `kind` | enum `set_priority` \| `set_affinity` \| `throttle` \| `restore` | ✅ | 动作类型（实际序列化为 camelCase：`setPriority` / `setAffinity` / `throttle` / `restore`，见 §0 枚举约定） |
| `targetPid` | int | ✅ | 目标进程 |
| `targetName` | string | ✅ | 目标进程名 |
| `parameters` | string(JSON) | ✅ | 动作参数（新优先级类/亲和掩码/限流值） |
| `previous` | string(JSON) | ❌ | 原值（用于恢复，`restore` 事件必填） |
| `result` | enum `succeeded` \| `failed` | ✅ | 执行结果 |
| `error` | string | ❌ | 失败原因，可空 |
| `durationMs` | long | ❌ | 生效持续时长（`restore` 时回填） |

## 7. rule.changed — 规则变更

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `tsMs` | long | ✅ | 变更时刻 |
| `ruleId` | string | ✅ | 规则 ID |
| `changeKind` | enum `added` \| `updated` \| `removed` \| `enabled` \| `disabled` | ✅ | 变更类型 |
| `source` | enum `user` \| `suggestion` | ✅ | 变更来源 |
| `rule` | string(JSON) | ✅ | 规则全文（变更后的状态） |

## 8. policy.intervention_toggled — ProBalance 开关切换

ProBalance 开关（会话⑫定案）：只控制"自动干预执行"，遥测/日志/服务继续运行。
关闭开关时立即恢复全部生效干预（恢复动作另行产生 `policy.action` restore 事件）。

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `tsMs` | long | ✅ | 切换时刻 |
| `enabled` | bool | ✅ | 切换后的开关状态 |
| `source` | string | ✅ | 切换来源（当前仅 `app`；未来 CLI/规则建议可扩展） |

---

## 9. SQLite 落盘形态（v1.1，2026-08-15 双表分层定案）

**分层原则（M3 定案）**：数据按"决策价值衰减速度"分两层——
高频采样（99% 写入量）价值衰减极快，只存短期 Buffer（够决策 + 最近诊断）；
低频日志（1% 写入量）价值长期存在，长保留（审阅/诊断/AI 语料）。

### 9.1 事件路由规则

| 表 | 事件类型 | 保留期 | 用途 |
|---|---|---|---|
| `samples`（热） | `sample.cpu` / `sample.memory` | **1 小时**（默认） | 决策输入（滑动窗口 5s）、"为什么卡"近期快照 |
| `event_log`（冷） | `process.lifecycle` / `policy.decision` / `policy.action` / `rule.changed` / `ui.foreground` | **30 天**（默认） | 操作日志审阅、长期诊断、未来 AI 语料 |

### 9.2 表结构（两表同构，物理分离）

```sql
CREATE TABLE IF NOT EXISTS samples (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ms   INTEGER NOT NULL,          -- Unix 毫秒 UTC
    type    TEXT    NOT NULL,          -- 事件类型枚举
    payload TEXT    NOT NULL           -- JSON，含该事件全部业务字段
);
CREATE INDEX IF NOT EXISTS idx_samples_ts_type ON samples (ts_ms, type);
CREATE INDEX IF NOT EXISTS idx_samples_type   ON samples (type);

CREATE TABLE IF NOT EXISTS event_log (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_ms   INTEGER NOT NULL,
    type    TEXT    NOT NULL,
    payload TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_event_log_ts_type ON event_log (ts_ms, type);
CREATE INDEX IF NOT EXISTS idx_event_log_type   ON event_log (type);
```

- 写入：按事件类型路由到对应表（`TelemetryTableRouter` 单一事实来源）
- 查询：按类型自动路由；跨表查询（回放/全量统计）用 UNION
- 回放 = 目标表 `ORDER BY ts_ms ASC`，按 `type` 过滤即得单一事件流
- 查询进程轨迹 = `WHERE type = 'sample.cpu' AND json_extract(payload, '$.pid') = ?`
- **清理循环**：service 周期执行（默认每小时）——
  - `samples` 删除 `ts_ms < now - SamplesRetentionMs`（默认 1h）
  - `event_log` 删除 `ts_ms < now - EventLogRetentionMs`（默认 30d）
- 保留期配置化（`StorageConfig`），可用户配置（SPEC §7 定案）

## 10. 隐私红线（SPEC §7 引用）

- 数据默认本地存储，不上云
- 未来任何上传必须：脱敏（进程名 + 聚合统计，不含路径/窗口标题）＋ 用户主动点击触发
- `policy.decision.inputSnapshot` 序列化时遵循脱敏规则（不落盘窗口标题等敏感字段）
