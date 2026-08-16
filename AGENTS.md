# AGENTS.md — Chief Process Officer (CPO) 项目工作须知

> 本文件由 DSH `dsh-agent-instructions` 插件在每个会话自动加载（项目根 `.git` 标记发现），
> 作为持久上下文基线。**改动本文件 = 改动所有未来会话的默认上下文**，保持精炼、准确、最新。

## 项目是什么

AI 驱动的 PC 性能管家（主动性优化，不是显示工具）。v1 无 AI，但架构从第一天面向 AI 扩展。
C# / .NET 8 + WinUI 3，GUI（普通用户）+ 引擎服务（管理员）分离，本地 SQLite 遥测，不上云。

**文档层级（新会话必读顺序）**：
1. `docs/DISCUSSIONS.md` — 讨论记录 + 环境备注 + 每阶段坑（**上下文恢复入口**）
2. `docs/SPEC.md` — 产品唯一事实来源（定位/铁律/架构/里程碑）
3. `docs/schema.md` — 遥测事件 schema v1.1（双表分层落盘契约，改动需同步代码）

## 工程结构

```
Cpo.sln
├─ app/         WinUI 3 壳（薄 UI，普通用户权限；M3 起经 gRPC 取数，不直读 SQLite）
├─ service/     Cpo.Service 控制台宿主（管理员；TelemetryRecorder 采集 + PolicyRunner 策略 + gRPC 服务端）
├─ core/        Cpo.Core 纯逻辑（遥测模型/双表存储/规则/引擎/回放，零 OS 依赖，xUnit 全覆盖）
├─ interop/     Cpo.Interop P/Invoke 隔离层（采样 + 进程控制，依赖 core 的接口）
├─ contracts/   Cpo.Contracts gRPC proto 契约（service+app 共用）
├─ tests/       xUnit 单测（当前 127 个全绿 = 质量门禁）
├─ tools/       演示/诊断工具（ReplayDemo 等）
└─ docs/        SPEC / DISCUSSIONS / schema / ADR
```

核心架构：确定性屏障 —— `PolicyEngine`（纯逻辑决策）只产建议进 `ProposalBus`，
执行只经 `ExecutionPath`（记录原值、超时/退出自动恢复、冷却防抖）；AI 未来只走建议通道。

## 构建 / 测试 / 运行（本地质量门禁，提交前必须全绿）

```powershell
# 必须先刷新 PATH（DSH 会话环境与系统不一致，见环境备注）
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')

dotnet build Cpo.sln -c Debug -p:Platform=x64      # WinUI 项目必须显式 x64，AnyCPU 无效
dotnet test tests/Cpo.Tests/Cpo.Tests.csproj -c Debug   # 全绿（130/130）才允许提交

# service 实机运行（遥测录制 + 策略评估 + gRPC 管道）
service/Cpo.Service/bin/Debug/net8.0/Cpo.Service.exe [--interval-ms=2000] [--engine=auto|supervised] [--rules=<json>]

# app 打包运行（绝不能直接跑 exe；连接管道 cpo-telemetry-<user>）
cd app/Cpo.App && winapp run . --detach
```

- **禁止**：直接运行打包 exe（必须 winapp run）、AnyCPU、删 Package.appxmanifest
- **CI 触发策略（2026-08-15 定案）**：日常 push/PR **不触发** CI；仅打 tag（`v*`）或 GitHub Actions 手动 Run workflow。CI 目前有报错待 M3 修复，本地门禁是唯一依赖。

## 环境备注（本机，DSH 会话内）

- **沙箱权限**：winget / dotnet build/test / winapp 等需要写工作区外或联网的命令，DSH 会话需 `danger-full-access`（受限模式报 SSPI/凭据/写入错误）。会话级文件策略为 danger-full-access 时无需显式指定。
- **winget 包 ID**：WinApp CLI 是 `Microsoft.WinAppCli`（注意大小写），不是 WinAppCLI。
- **git 推送**：必须用 Windows 原生 OpenSSH（`$env:GIT_SSH='C:\Windows\System32\OpenSSH\ssh.exe'; $env:GIT_SSH_VARIANT='ssh'`），PortableGit 的 msys ssh 在沙箱下崩溃。SSH 认证间歇性失败属已知问题，重试即可。git 身份：`miko <modmi@qq.com>`。
- **网络**：本机经 Clash 代理（127.0.0.1:7897），受限沙箱会拦截 SSL/凭据。
- **git 提交**：`git -c user.name="miko" -c user.email="modmi@qq.com" commit -m "..."`；push 时 `git push --tags` 才推 tag。

## 关键坑速查（详细原因见 DISCUSSIONS）

| 坑 | 对策 |
|---|---|
| 事件序列化 | payload = camelCase 业务字段（无 type），type 独立列；用 `TelemetryEventSerializer`，禁用 STJ 多态判别符 |
| 枚举 JSON | 必须 `JsonStringEnumConverter(CamelCase)`（RuleStore 等） |
| SQLite 双表 | `samples`（热，1h）+ `event_log`（冷，30d）分层；路由用 `TelemetryTableRouter`；清理循环在 service PurgeLoopAsync |
| SQLite 并发/性能 | **必须 WAL**（SchemaSql 前置 `PRAGMA journal_mode=WAL; synchronous=NORMAL`）：rollback journal 下写持 EXCLUSIVE 锁阻塞所有读（评估窗口/UI 查询），写慢→评估延迟→决策滞后（会话⑳e）；**purge 必须分批**（`id IN (SELECT id … LIMIT 10000)` 循环），单语句 DELETE 百万行持锁秒级 = 周期性采样停摆；**计数用 store 内存计数**（Initialize 基线 + Append 增 + Purge 减），禁 COUNT 全表（30 天后 event_log 千万行秒级）；评估窗口查询必须带 `Type`/`TypePrefix` 收敛单表（无类型条件走 UNION 全量反序列化） |
| SQLite 查询 | 取最近 N 条用 `Descending=true`；前缀用 `TypePrefix`；进程过滤用 `json_extract(payload,'$.pid')`；UNION 查询外层包装再 ORDER BY ts_ms；**排序必须带次级键破平**（同 ts_ms 事件：单表 `id` / 跨表 UNION `type`），否则轮询间顺序翻转 → 增量合并把行删掉重插（列表闪烁） |
| WinUI 小列表增量更新 | 行数有限（如 20~200）的日志列表：ItemsPanel 用非虚拟化 `StackPanel`，避免顶部插入时容器回收导致视口行闪烁；数据源排序必须确定性（见上一条） |
| SQLite 内存库 | 测试用 `file:xxx?mode=memory&cache=shared`（`:memory:` 每连接独立）；**DisposeAsync 的 ClearAllPools 是全局的**——并行测试类要加 `[Collection("NonParallelGrpc")]` 串行 |
| P/Invoke | 用 `DllImport`（非 LibraryImport），宽字符 API 必须 `CharSet.Unicode` |
| GetSystemTimes | **kernel 含 idle**——系统 CPU 累计必须 `kernel+user−idle`，否则系统 CPU% 恒≈100%（启发式"饱和≥90%"恒真 → 非饱和期持续误降常驻挤占者如 chrome，会话⑳f 实机教训）；进程级 GetProcessTimes 无此问题 |
| 进程优先级类传染 | **Windows ProBalance 会降后台进程为 BelowNormal，且子进程继承父进程优先级类**——一旦终端/宿主被降，其启动的一切进程全 BelowNormal；而 `ThreadPriority.Highest` 是相对进程类的（BelowNormal base 6 + Highest = base 8），⑳d 抗饿死防护被抵消。**service/app 必须启动自保 + 周期自检**（`EnsureProcessNormalPriority`：进程类非 Normal 即升回，每评估/轮询轮一次，成本一次 P/Invoke，会话⑳g 实机教训） |
| 策略输入 | 固定滑动窗口（15s，会话⑳c 放宽——饱和时采样滞后实测超 5s）+ 每进程最新样本，**不要**增量窗口（采样落库有滞后） |
| 干预防抖 | 恢复后 DurationMs 内冷却（`_lastRestoredMs`），恢复时刻由调用方传入 |
| ProBalance 开关 | 执行干预 ⇔ `Mode==Automatic && InterventionEnabled`（volatile 字段）；关闭 = 立即 RestoreAll 并留痕；切换事件 `policy.intervention_toggled`（schema §8）；App ToggleSwitch TwoWay 绑定必须配同步标志（`SyncInterventionEnabled`）防程序刷新误触发 gRPC |
| 启发式（响应性保护） | 目标 = OS/前台响应性，降 CPU 是手段不是指标（会话⑲）；触发三条件齐备：系统饱和（≥90%）+ 进程挤占（≥50%）+ 非关键（非前台/非系统关键名单，**含引擎自身 cpo.service/cpo.app**）；无前台信息 = 整体保守跳过；规则永远优先。**两档保护**（⑳c）：前台进程本身绝不降 / 近期前台（1h，仅前台本身 pid）温和降（80% + 10s）/ **其余含前台子进程**（rg/编译等，降了不影响前台响应）标准降 50% + 30s；**条件解除提前恢复**（启发式干预 CPU 已低 → 立即恢复，规则干预除外） |
| service 异常防护 | **所有采样/评估/清理循环必须 try/catch**（实机教训：TelemetryRecorder 曾因一次 SQLite disk I/O error 未处理异常裸崩，遥测停摆；EvaluateLoop/PurgeLoop/Gatekeeper 都有，唯独 recorder 漏了）；Program.Main 全局兜底 catch 走正常收尾 |
| 采样饱和失效 | ~~系统 100% 饱和时采样器饿死~~ **已修**（会话⑳d）：采样/评估/app 轮询线程 `ThreadPriority.Highest`（Normal 类 base 10 > 普通 8）+ ProcessSampler 父进程查表去 O(N²)（每进程独立 Toolhelp32 全表扫描 → 一轮快照）；残余：风暴进程为 HIGH 类（base 13）仍会饿采样，v1 接受 |
| gRPC named pipes | 契约在 `contracts/Cpo.Contracts`；服务端 `UseNamedPipes(o => o.CurrentUserOnly=true)` + `ListenNamedPipe("cpo-telemetry-<user>")`；客户端 `GrpcChannel.ForAddress` + SocketsHttpHandler.ConnectCallback 返回 NamedPipeClientStream（无 TransportType 选项）；**gRPC 是传输层，事件信封 payload_json 复用 schema JSON** |
| proto 定义顺序 | **service 引用的 message 必须先定义**（Grpc.Tools 2.67 实测：message 在 service 之后报 "not defined"，前向引用不被接受）；注意 RPC 名与 message 名前缀相反易写反（`ReportForeground` vs `ForegroundReportResponse`） |
| gRPC 安全 | 默认 ACL 允许同用户任意进程连接！必须 `CurrentUserOnly=true`（防其他用户）+ `AuthInterceptor` 会话令牌校验（防同用户任意进程）；**会话令牌来自门卫管道 `cpo-gate-<user>`**（握手时 `GetNamedPipeClientProcessId` 校验对端必须是 Cpo.App.exe → 发放 256-bit 内存令牌，12h 不落盘；文件令牌已废弃）；客户端先握手再调用，`Unauthenticated` 时清令牌自动重握手 |
| gRPC 服务类命名 | proto 生成的静态类 `TelemetryService` 占用类名 → 实现类用 `TelemetryGrpcService` |
| FileSecurity | 在 `System.IO.FileSystem.AccessControl` 包，命名空间 `System.Security.AccessControl`；用 `FileInfo.SetAccessControl` 而非 `File.SetAccessControl` |
| WebApplication | 必须 `builder.Services.AddXxx` 后 `builder.Build()`；Services 属性只读 |
| 打包 app 数据 | LocalAppData 被虚拟化到 `%LOCALAPPDATA%\Packages\<PFN>\LocalCache\Local`——M3 起 app 走 gRPC 不再直读文件 |

## 工作流约定

- 里程碑：M1 骨架+遥测 ✅ → M2 策略引擎+回放 ✅ → M3 启发式+UI → M4 打包发布（SPEC §8）
- 每次重大变更：更新 `docs/DISCUSSIONS.md`（追加会话节）+ 同步 `SPEC.md`（如适用）
- 提交粒度：按里程碑/修复主题，Conventional 风格中文消息（`fix(app): ...` / `M2: ...`）
- 隐私红线：遥测数据默认本地，任何上传需脱敏 + 用户主动触发
