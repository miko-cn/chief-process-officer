# CPO — Chief Process Officer

> **Chip-Process-Optimizer**：AI 驱动的 PC 性能管家 —— 主动性优化，而不是显示工具。

![Platform](https://img.shields.io/badge/platform-Windows%2010%201809%2B%20%7C%20Windows%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![UI](https://img.shields.io/badge/UI-WinUI%203-8A2BE2)
![Tests](https://img.shields.io/badge/tests-138%2F138-2ea44f)
![License](https://img.shields.io/badge/license-MIT-yellowgreen)

---

## English Overview

**CPO (Chip-Process-Optimizer)** is an AI-ready PC performance manager for Windows. Unlike traditional process viewers, it is **proactive**: it watches for CPU storms (compilers, containers, AI/CLI tool chains), automatically deprioritizes the background processes before they steal your responsiveness, and **restores everything to its original state** once the pressure is gone.

Every intervention is logged, explainable, and reversible. The v1 engine is a **deterministic policy engine** (no AI), but the architecture is AI-ready from day one — AI suggestions will only ever flow through the proposal bus, never the execution path.

- **Status**: M1 ✅ M2 ✅ · M3 in progress（启发式 v1 ✅ · 三层安全 ✅ · ProBalance 开关 ✅ · 日志审阅 ✅ → 剩余：时间线网格 / 进程表 / 推送化）
- **Language**: C# / .NET 8 · **UI**: WinUI 3 (Fluent) · **Transport**: gRPC over named pipes
- **Data**: local SQLite, dual-tier telemetry — privacy-first, never leaves your machine

---

## 产品简介

电脑卡顿的时候，谁在抢资源？CPO 不只是把数字显示给你看，而是**在你感到卡顿之前主动介入**：

- 检测到后台进程的 CPU 风暴（编译、索引、容器、下载、AI/CLI 工具风暴）→ **自动降优**（优先级 + CPU 亲和性）
- 压力解除、超时或引擎退出 → **自动恢复原值**，不留任何后遗症
- 每次干预**全量留痕**（做了什么、为什么、持续多久、恢复没有），你可以在审阅面板里回看

对标 Process Lasso 的 ProBalance，但赢在**深度**（规则化 + 可解释 + 自动化档案）与**现代体验**（WinUI 3 / Fluent）。

> 仓库名 `chief-process-officer` 是工作名；产品定名 **CPO（Chip-Process-Optimizer）**。

## ✨ 特性

- 🧠 **主动性优化**：启发式自动干预（响应性保护——系统饱和 + 进程挤占 + 非关键三条件齐备才介入），而非被动显示
- 🛡️ **前台保护**：前台进程本身绝不降级；近期前台（1h）温和降级；其子进程（rg/编译工具等）标准降级——降子进程不影响前台响应
- ⚙️ **统一策略引擎**：显式规则（进程名通配符匹配）与启发式是同一引擎的两个配置面，规则优先
- 🔄 **干预可恢复**：记录原值（优先级类 / 亲和性掩码），条件解除、超时、引擎退出时自动恢复，含冷却防抖；**干预队列持久化落盘，service 强杀/崩溃后启动自动恢复残留**
- 📝 **决策留痕**：`policy.decision` / `policy.action` 全量落库，人类可读 + 机器可读 JSON 双视图
- 🎬 **回放框架**：真实负载轨迹离线回放，逐帧评估策略 —— 策略调优不需要等真机卡顿
- 📊 **遥测即一等公民**：结构化事件流 + 文档化 schema（`docs/schema.md`），SQLite 双表分层存储
- 🔐 **本地优先，默认不上云**：隐私红线写进产品承诺，未来 AI 也只上传脱敏聚合数据且用户主动触发
- 🪟 **Windows 10 1809+ / Windows 11**：单套 Fluent UI，Win11-only 特效（Mica 等）在 Win10 自动降级

## 🧱 架构

**进程模型**：GUI（普通用户权限）+ 引擎服务（管理员权限）分离 —— 进程控制需要提权，GUI 常年提权不现实（同 Process Lasso 架构）。

**确定性屏障**：引擎（纯逻辑决策）只产建议进 `ProposalBus`，执行只经 `ExecutionPath`（记录原值、超时/退出自动恢复、冷却防抖）。AI 未来只走建议通道，代码结构强制隔离。

```
用户显式规则 ──┐
进程遥测 ──────┼──► PolicyEngine（纯逻辑决策，可单测）
系统负载 ──────┤        │
前台状态 ──────┘        ▼
                   ProposalBus（建议总线）
                         │
                         ▼
                   ExecutionPath（执行路径：记录原值 → 干预 → 自动恢复）
                         │
                         ▼
              决策日志 + 遥测事件流（SQLite 双表分层）
```

```
┌──────────────────────────────────────────────────────────────┐
│ app/  Cpo.App（WinUI 3 壳，普通用户权限）                      │
│   操作日志审阅面板 · 状态卡片 · 全局开关 · 前台检测              │
└───────────────────────────┬──────────────────────────────────┘
                            │ gRPC over named pipes（cpo-telemetry-<user>）
                            │ 三层安全：管道 ACL + 会话令牌 + 门卫对端校验
┌───────────────────────────▼──────────────────────────────────┐
│ service/  Cpo.Service（管理员宿主）                            │
│   TelemetryRecorder 采集 → PolicyRunner 评估 → ExecutionPath   │
└───────────────────────────┬──────────────────────────────────┘
┌───────────────────────────▼──────────────────────────────────┐
│ core/  Cpo.Core（纯逻辑，零 OS 依赖，xUnit 全覆盖）             │
│   遥测模型 · 双表存储 · 规则 · 策略引擎 · 回放 · 决策日志         │
└───────────────────────────┬──────────────────────────────────┘
                            │ P/Invoke 隔离在 interop/
┌───────────────────────────▼──────────────────────────────────┐
│ interop/  Cpo.Interop（Toolhelp32 · GetProcessTimes ·         │
│   SetPriorityClass · SetProcessAffinityMask · 命名管道）       │
└──────────────────────────────────────────────────────────────┘
```

### 仓库布局

```
Cpo.sln
├─ app/         WinUI 3 壳（薄 UI，普通用户权限；经 gRPC 取数，不直读 SQLite）
├─ service/     Cpo.Service 控制台宿主（管理员；采集 + 策略 + 执行 + gRPC 服务端）
├─ core/        Cpo.Core 纯逻辑（遥测模型 / 双表存储 / 规则 / 引擎 / 回放，零 OS 依赖）
├─ interop/     Cpo.Interop P/Invoke 隔离层（采样 + 进程控制）
├─ contracts/   Cpo.Contracts gRPC proto 契约（service + app 共用）
├─ tests/       xUnit 单测（138 个全绿 = 质量门禁）
├─ tools/       演示 / 诊断工具（ReplayDemo、demo-rules.json 等）
└─ docs/        产品规格 / 讨论记录 / 遥测 schema
```

## 🛠 技术栈

| 层 | 选型 |
|---|---|
| 语言 / 运行时 | C# / .NET 8 |
| UI | WinUI 3（Windows App SDK）· Fluent Design |
| GUI ↔ 服务通信 | gRPC over named pipes（本地进程间，不走网络栈） |
| 遥测存储 | SQLite（双表分层：`samples` 热数据 1h / `event_log` 冷数据 30d） |
| 测试 | xUnit |
| CI | GitHub Actions（Windows 10 / Windows 11 矩阵） |

## 🚀 快速开始

### 环境要求

- Windows 10 1809+ 或 Windows 11
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- 可选：[winapp CLI](https://github.com/microsoft/winapp)（打包运行 GUI，`winget install Microsoft.WinAppCli`）

### 构建与测试

```powershell
git clone https://github.com/miko-cn/chief-process-officer.git
cd chief-process-officer

# 构建（注意：WinUI 3 项目必须显式指定 x64，AnyCPU 无效）
dotnet build Cpo.sln -c Debug -p:Platform=x64

# 运行单测（质量门禁：必须 138/138 全绿）
dotnet test tests/Cpo.Tests/Cpo.Tests.csproj -c Debug
```

### 运行

```powershell
# 1. 启动引擎服务（遥测采集 + 策略评估 + gRPC 管道；管理员权限可完整控制其他进程）
service/Cpo.Service/bin/Debug/net8.0/Cpo.Service.exe --engine=auto --rules=tools/demo-rules.json

# 2. 启动 GUI（打包运行，绝不能直接跑 exe）
cd app/Cpo.App
winapp run . --detach
```

服务常用参数：`--interval-ms=2000`（采样间隔）、`--engine=auto|supervised`（自动执行 / 只记录不执行）、`--rules=<json>`（规则文件路径）。

### 快速演示（亲眼看到主动干预）

```powershell
# 生成 CPU 压力（两个死循环进程）
powershell -Command "while($true){}"
powershell -Command "while($true){}"
```

配合 `tools/demo-rules.json` 中的演示规则，引擎会自动将 `powershell` 降为 BelowNormal 优先级；压力结束后自动恢复。打开 app 的操作日志面板即可看到完整的决策 → 执行 → 恢复留痕。

## 🧪 测试与质量门禁

- 本地门禁：`dotnet build` 0 错误 + `dotnet test` **138/138 全绿**，提交前必须通过
- 核心测试面：CPU 计算、进程生命周期、规则匹配、策略引擎、执行路径（恢复/冷却/幂等）、回放框架、SQLite 双表路由、gRPC 管道认证与门卫校验
- CI 触发策略：日常 push / PR **不触发** CI；打 tag（`v*`）或 GitHub Actions 页面手动触发时运行（编译 + 单测 + Win10/Win11 矩阵）

## 🔐 安全与隐私

**通信安全（gRPC 三层纵深）**：

1. **管道 ACL**：named pipe 限制为当前用户（防其他用户/服务）
2. **会话令牌**：256-bit 随机内存令牌（12h 有效，不落盘），所有 RPC 经拦截器校验
3. **门卫管道**：握手时校验对端进程必须是 `Cpo.App.exe`（`GetNamedPipeClientProcessId` + 可执行文件路径校验）→ 通过才发放会话令牌

**干预安全**：只调用文档化 Win32 API（`SetPriorityClass` / `SetProcessAffinityMask`，与 Process Lasso / BES 同款行为），不注入、不 hook 其他进程；每次干预记录原值并自动恢复。

**隐私红线**：遥测数据默认全本地存储，不上云；未来任何上传必须脱敏（仅进程名 + 聚合统计，不含路径/窗口标题）且用户主动触发。

## 🗺 路线图

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | 项目骨架 + CI + WinUI 3 壳 + 遥测采集 + schema 定稿 | ✅ 完成 |
| M2 | 回放框架 + 决策日志 + 基础策略（显式规则优先）+ 自动恢复 | ✅ 完成 |
| M3 | 启发式 v1（自动执行）+ 现代化 UI + gRPC 通信与安全 | 🚧 进行中 |
| M4 | Windows 服务化（开机自启）+ 打包签名 + 正式发布 | ⏳ 计划 |

**M3 已落地**：启发式 v1（响应性保护：系统饱和 + 挤占 + 非关键三条件齐备才介入；前台进程/近期前台两档保护；条件解除提前恢复）、gRPC over named pipes + 三层安全（管道 ACL + 会话令牌 + 门卫对端进程校验）、ProBalance 全局开关（关闭 = 立即恢复全部干预并留痕）、前台检测上报（SetWinEventHook → 引擎前台保护输入）、操作日志审阅面板（增量刷新、断线自动重连、确定性排序）、干预队列持久化（强杀/崩溃后启动自动恢复残留）。

**M3 剩余**：三区审阅面板的时间线网格图（XAML Polyline，零依赖）与全进程视图（虚拟化列表 + 最新样本快照 RPC）、WatchEvents 内存广播推送（替代 500ms DB 轮询假推送）。

**未来（AI 扩展点，v1 只留接口）**：

- 诊断解释层：LLM 深度诊断（脱敏快照 + 用户主动触发）
- 规则建议层：LLM 观察使用习惯 → 提议规则 → 用户确认（AI 只提议，不执行）
- 调参闭环：agent + 遥测回放自动寻参
- 场景档案：游戏模式 / 办公模式自动切换

## 📚 文档

- [产品规格 SPEC](docs/SPEC.md) — 定位 / 铁律 / 架构 / 里程碑（产品唯一事实来源）
- [遥测 Schema](docs/schema.md) — 事件契约与 SQLite 落盘形态
- [讨论记录 DISCUSSIONS](docs/DISCUSSIONS.md) — 决策过程与开发坑位记录

## 🤝 贡献

欢迎任何形式的贡献（issue、PR、讨论）。请遵循：

- 提交前本地门禁全绿：`dotnet build` + `dotnet test` 138/138
- 提交信息使用 Conventional Commits 风格（中文描述，如 `fix(app): ...`、`M2: ...`）
- 重大变更同步更新 `docs/`（SPEC / DISCUSSIONS / schema）
- CI 仅在打 tag（`v*`）或手动触发时运行，日常迭代不阻塞

## 📄 许可证

[MIT](LICENSE) © 2026 Miko

---

*🖼 截图与演示视频待补充（M3 UI 完善后更新）。*
