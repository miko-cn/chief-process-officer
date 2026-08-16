# 讨论记录（DISCUSSIONS）— 上下文恢复用

> 本文件记录产品立项前的完整讨论过程与决策，供任何未来会话快速恢复上下文。
> 新会话恢复顺序：**先读本文件，再读 [SPEC.md](./SPEC.md)**。
> 规范：每次讨论/决策追加一节，标日期；重大变更在 SPEC.md 同步。

---

## 2025-08-13 会话 ① — 仓库连接与环境修复（已完成）

**背景**：用户要求将本地空文件夹与 `git@github.com:miko-cn/chief-process-officer.git` 连接。

**过程要点**：
- 本机网络经 Clash TUN 代理（fake-IP 198.18.x.x），**端口 22 被拦截**，SSH 直连 github.com 失败
- 解决：`~/.ssh/config` 将 `github.com` 路由到 `ssh.github.com:443`（GitHub 官方备用端口），指定 `~/.ssh/id_ed25519`（已验证认证为 miko-cn）
- 代理导致 SSH 认证**间歇性失败**（约 3/4 概率密钥交换被破坏，报 `Permission denied (publickey)`）
- 解决：ssh 配置加 `ControlMaster auto` + `ControlPath ~/.ssh/cm-%r@%h:%p` + `ControlPersist 30m`——主连接认证成功后复用通道，不再重复认证
- 仓库状态：`git init -b main`，remote `origin` = `git@github.com:miko-cn/chief-process-officer.git`，`main` 跟踪 `origin/main`，内容 = 远程提交 `08b3a26 Initial commit`（LICENSE、README.md）
- git 身份已配置（全局 `~/.gitconfig`）：`user.name=miko`，`user.email=modmi@qq.com`

**环境备注（未来会话必读）**：
- git push/fetch 前若遇 `Permission denied (publickey)`：重试一次；若持续失败，重建主连接：`ssh -N -f git@github.com`（可循环重试直到成功），成功后 git 操作走复用通道
- **本机 git push 若报 msys 崩溃（`couldn't create signal pipe, Win32 error 5`，栈帧在 PortableGit 的 ssh.exe/sh.exe + msys-2.0.dll）**：这是 DSH 沙箱限制命名管道导致 PortableGit 的 msys ssh 无法启动，**不是认证问题**。解法：用 Windows 原生 OpenSSH——`$env:GIT_SSH="C:\Windows\System32\OpenSSH\ssh.exe"; $env:GIT_SSH_VARIANT="ssh"; git push ...`（2026-08-15 验证有效）。不要用 `core.sshCommand` 配置，git 会用 sh 包装执行、仍触发 msys 崩溃
- 本机 `192.168.31.40:3322` 有用户自建 Forgejo（用户 yumiko），**无**本仓库镜像，勿混淆
- 本机无 gh CLI、无 HTTPS 凭据；SSH 是唯一已验证的 push 通道

---

## 2025-08-13 会话 ② — superpowers-zh 技能包（决策：不安装）

**背景**：用户问是否需要安装 superpowers-zh（Claude Code 技能包中文版，多个 fork）。

**结论**：**不安装**（用户确认）。理由：
- 当前任务不需要；DSH 内置能力（todo/子代理/workflow/goal）已覆盖其大部分方法论价值
- 未来如需：DSH 技能格式兼容（`<name>/SKILL.md` + YAML frontmatter），可装 `~/.dsh/skills`（全局）或项目 `.dsh/skills`（仅本仓库），无需改代码

---

## 2025-08-13 会话 ③ — 产品立项讨论（本仓库的核心内容）

### 3.1 初始想法
用户用 Process Lasso 解决电脑卡顿，觉得好用但：UI/交互落后于时代、企业订阅费贵 → 想**复刻一个现代化版本**。

### 3.2 我的判断：复刻必死，换代才有机会
- 免费替代太强（System Informer 开源碾压、BES 免费限流）→ 价格战打不赢
- Lasso 护城河是 15 年边角案例迭代的启发式，不是功能清单
- 杂活多：驱动签名、杀软误报、UAC、Win11 兼容
- 但"现代化系统性能工具"生态位是空的；企业订阅贵 = 付费意愿存在；用户自己就是目标用户

### 3.3 技术可行性（已核实，文档化 API，无需逆向）
- 优先级：`SetPriorityClass`；亲和性：`SetProcessAffinityMask`
- CPU 限流：Job Object `JOBOBJECT_CPU_RATE_CONTROL_INFO`（Win8+，BES/Lasso 同款机制）
- 进程监控：WMI/PDH、ETW 生命周期事件
- 有经验者 + AI 辅助，MVP 2~4 个月；**v1 不需要内核驱动**

### 3.4 用户画像决策（Q&A 确认）
- **目标**：副业收入 + 优秀产品；核心目标 = **主动性优化**（ProBalance 式启发式），非显示
- **时间**：周末型 + 工作日白天远程指挥 agent 自主开发
- **用户选择"开发者/生产力人群"**：编译卡顿、IDE 索引、容器/下载抢资源；付费不心疼；社区获客
- **用户选择"直接上启发式引擎"**：不搞规则引擎 + 启发式两套系统，统一为 Policy Engine（规则 = 最高优先级输入，启发式 = 默认决策函数）

### 3.5 信任设计（启发式最大风险的对策）
- 默认**监督模式**：引擎只建议（"检测到 X 占 CPU 62%，建议降优先级，预计收益 Y"），用户一键采纳
- 用户对某类别建立信任后可升级**自动模式**
- 决策日志做成可视化回放面板 = 信任工具 + 营销素材
- 启发式 v1 必须零误伤口碑

### 3.6 AI 的定位（用户追问后澄清）
- **核心引擎绝不叫 AI**：启发式是确定性算法，包装成 AI 会被开发者用户看穿、信任归零
- AI 在产品中的真实价值三层：
  1. **诊断解释层**：卡顿快照 → 一句话人话结论（Lasso 永远做不出来；补可解释性）
  2. **规则建议层**：观察使用习惯 → 提议规则 → 用户确认（AI 只提议不执行）
  3. **调参闭环**（内部）：agent + 遥测回放自动寻参 = 一个人对抗大厂迭代速度的杠杆
- 架构红线：**确定性屏障** —— ProposalBus（建议）与 ExecutionPath（执行）物理分离

### 3.7 v1 决策（用户拍板）
**"v1 先不考虑 AI，但做好面向 AI 的准备"**，具体四件事：
1. **遥测即一等公民**：结构化事件流 + 文档化 schema + SQLite 本地存储（AI 的弹药，现在开始囤）
2. **决策日志机器可读**：人类可读 UI + 机器可读 JSON 双视图（未来 = AI 训练语料）
3. **三个 AI 扩展点接口占位**：`IDiagnosticExplainer` / `IRuleSuggester` / `TuningHarness`，v1 朴素实现，未来换实现不重构
4. **确定性屏障**从第一天立起

### 3.8 技术选型（用户问 Rust 后决策）
**C# / .NET 8 + WinUI 3**。理由：
- 安全风险不在 Rust 的射程内：核心 API 全是文档化用户态 Win32 调用，C# P/Invoke 两行搞定；真正的危险区（内核驱动）v1 不做
- Windows UI：WinUI 3 是 Win11 官方 Fluent 方向；Rust 的 egui/iced 是开发者工具美学，Tauri 则变 Web UI；C++/WinUI 3 是地狱；Qt 有授权成本
- Agent 协作效率（决定性）：C#/.NET 文档与训练语料 10 倍于 Rust；GitHub Actions windows runner 零配置；C# 可读性对周末审查友好
- 未来若引擎成瓶颈可单独 Rust 重写（架构留口子）

### 3.9 里程碑与商业化（定稿）
- 里程碑 M1~M4 共 **4~6 个月**（见 SPEC §8），M1 验收 = "能录制本机负载轨迹"（遥测是地基，顺序不能反）
- 免费版（监控 + 手动 + 3 条规则）/ Pro 买断 $19.9~29.9 / 企业版远期订阅
- 分发：MSIX 旁加载或经典安装器，**不用 Microsoft Store**（沙箱限制管理员级进程控制）
- 进程模型：GUI（普通用户）+ 引擎服务（管理员）分离，同 Lasso 架构

### 3.10 已定决策清单（ADR 摘要）
| # | 决策 | 理由 |
|---|---|---|
| D1 | 定位：新一代 PC 性能管家，非 Lasso 复刻 | 免费替代 + 生态位空缺 |
| D2 | 目标用户：开发者/生产力人群优先 | 信任自动判断、付费意愿、社区获客 |
| D3 | 主动性：直接上启发式，统一 Policy Engine | 规则与启发式是同一引擎两个配置面 |
| D4 | v1 无 AI，面向 AI 准备（遥测/日志/扩展点/屏障） | 确定性执行 + AI 建议，信任为先 |
| D5 | 技术栈 C#/.NET 8 + WinUI 3 | 生态、UI、agent 效率 |
| D6 | v1 无内核驱动 | 省签名费 + 工作量，Job Object 够用 |
| D7 | 遥测本地优先，不上云 | 开发者信任红线 |
| D8 | 监督模式 → 自动模式信任曲线 | 启发式误伤风险的唯一解 |
| D9 | GUI/服务分离 + 非 Store 分发 | 提权需求 vs 沙箱限制 |
| D10 | 每 PR 过 CI（编译+单测）铁律 | 周末审查 + 工作日 agent 模式的存活前提 |

### 3.11 待定事项（不阻塞开工）
- 正式产品名（工作名：Chief Process Officer）
- 免费版功能边界细节（规则条数等）
- 分发渠道最终选择（MSIX vs 经典安装器，M4 定）
- LLM 深度诊断（Pro）是否最终做、接哪家（v1 后评估）
- 游戏玩家/普通用户扩张顺序

### 3.12 下一步
- [x] 固化 SPEC.md + 本记录，提交推送（本会话）
- [ ] 用户周末审阅 SPEC，反馈异议
- [ ] 审阅通过后开工 M1：项目骨架 + CI + WinUI 3 壳 + 遥测采集

---

## 2026-08-15 会话 ④ — SPEC 待定项审阅（用户逐项拍板）

**背景**：用户要求"过一遍 SPEC 值得注意的待定项"，逐项给出决策，并询问若干技术细节。

### 4.1 已拍板决策（已同步 SPEC v0.2）

| # | 待定项 | 决策 |
|---|---|---|
| 1 | 正式产品名 | **CPO（Chip-Process-Optimizer）**，仓库名沿用 chief-process-officer |
| 2 | 免费版功能边界 | 免费版 = 进程监控 + 手动调整 + **预设的启发式规则**；**不含**规则自进化、**不含**数据接口（数据接口是企业/专业版卖点） |
| 3 | 目标用户 | **全人群**（不限于开发者/游戏玩家）：凡觉得电脑性能需优化、想提高系统响应度的都是用户，直接抢 Process Lasso 用户盘；获客切入点仍走开发者社区 |
| 4 | LLM 深度诊断 | v1 后再评估；但 **v1 就做好给 agent 的数据接口（CLI 或 MCP）**，遥测查询 + 决策日志导出 |
| 5 | 降优手段 | **优先级 + CPU 亲和性硬降为主**（不走 Job Object 限流为主力）；差异化场景：**AI/CLI 密集工具风暴**（grep/rg/编译器/模型工具等密集跑时）在吃满 CPU 前**提前降优 + 动态调整 + 事后恢复** |
| 6 | 采样频率与数据量 | **不一开始定死**：配置化 + 可测试化 + 用户可配置（企业需求） |
| 7 | EcoQoS / Win11 效率模式 | 不依赖（EcoQoS 后期再评估）；支持 WinUI 3 即可 |
| 8 | 权限原则 | 最少权限但不牺牲功能：GUI 普通用户；服务用能满足性能调控需求的最小权限 |
| 9 | 程序结构 | M1 定稿：**core + service 组合**（core = 纯逻辑零 OS 依赖；service = 管理员宿主） |

### 4.2 技术细节答复（本会话问答记录）

- **engine/ vs core+service 是什么意思**：engine/ = 单一"引擎"工程，逻辑与服务宿主混在一起；core+service = 拆两个工程——core 纯 C# 逻辑（遥测模型、策略引擎、决策日志，零 Win32 依赖，xUnit 全覆盖），service 是薄的宿主（P/Invoke、ETW、调度，把 OS 数据喂给 core、执行 core 决策）。选后者，理由：SPEC 铁律"策略引擎纯逻辑可单测"+ 回放测试需要 core 不依赖真实 OS。
- **遥测 schema 是什么**：每个事件的数据结构定义（像数据库表结构）：事件类型、字段名、类型、单位、时间戳格式。SPEC §7 只列了事件名，M1 要"定稿 schema"= 把每个事件的字段定义写死，所有组件（采集、存储、回放、未来 AI）按同一契约走。SQLite 表结构就是 schema 的落盘形态。
- **GUI↔服务通信机制**：推荐**命名管道（Named Pipe）**，首选 **gRPC over named pipes**（.NET 8 原生支持，强类型契约 + 双向流 + 低延迟），备选裸 named pipe + JSON。理由：纯本地不走网络栈 → 免疫防火墙/代理/本地 TUN 劫持（本机 Clash TUN 就是活例子）；自带 ACL 权限控制；无端口冲突。参考：微软 [gRPC 跨进程通信文档](https://learn.microsoft.com/zh-cn/aspnet/core/grpc/interprocess)（.NET 8 支持 named pipes）。
- **服务身份**：进程控制（SetPriorityClass/SetProcessAffinityMask）对别的进程需要管理员权限。最少权限方案：GUI 普通用户 + 服务以 LocalSystem 运行（能打开所有会话进程，功能不牺牲）；安全收紧（最小 Token、管道 ACL 只允许交互用户连接）放到 M2。不用 Store 分发正是因此（沙箱限制管理员级进程控制）。
- **Job Object 限流的兼容性坑（解释）**：① 一个进程只能属于一个 job，若目标进程已在别的 job 里（Chrome/Edge 每个进程都有自己的 job，系统组件、某些杀软也是）→ 塞不进去，限流失效；② job 会传染给子进程（放进 job 后子进程自动继承，想只限父进程做不到，除非设 breakaway，但 breakaway 默认关且影响安全）；③ job 内进程启动提权子进程（UAC 提升）会失败（受限 token 问题），某些应用会坏；④ 系统保护进程（PPL）动不了。→ 这正是 SPEC 说"Job Object 覆盖 90% 价值"的由来。**优先级+亲和性硬降没有这些坑**（直接对句柄操作，不需要收容进程），所以用户的选型是对的。
- **ui.foreground 具体怎么做（边界细节）**：
  - 基础：GUI 侧 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 订阅前台变化（比轮询 GetForegroundWindow 好，事件驱动）→ `GetForegroundWindow()` → `GetWindowThreadProcessId()` 拿 pid。
  - 多显示器：前台 = 键盘焦点所在窗口（GetForegroundWindow 返回的是接收键盘输入的窗口，天然正确），多屏上"活跃窗口"以焦点为准。
  - 全屏游戏：独占全屏/无边框窗口 GetForegroundWindow 通常仍能拿到（返回游戏窗口或其子类）；个别老游戏需兜底（结合 GetGUIThreadInfo 查活动窗口）。
  - 无边框窗口：正常返回；UWP 现代应用返回 ApplicationFrameHost 或应用顶层窗口，需 GetApplicationUserModelId 才能拿到真实包名。
  - 焦点被"偷"：后台程序调 SetForegroundWindow（Windows 有前台锁限制但存在）→ 需加**停留时长阈值**（如焦点持续 ≥200ms~1s 才算切换）+ 结合 GetLastInputInfo（键盘/鼠标最后输入时间）判断真实交互。
  - 服务在 Session 0 拿不到用户桌面前台 → **前台检测必须在 GUI 侧做**，经命名管道上报给服务。
- **MSIX vs 经典安装器利弊**：
  - **MSIX**：利 = 干净安装/卸载、权限模型清晰、可自动更新（App Installer）、适合 Store 分发；弊 = 旁加载需签名证书受信或开开发者模式（对普通用户是门槛）；包内服务受限（MSIX 应用装 Windows 服务很别扭，服务需独立安装器引导）；注册表/文件系统虚拟化会干扰管理员级工具；升级安装包里的服务组件麻烦。
  - **经典安装器（Inno Setup/WiX）**：利 = 完全控制（装服务、注册表、计划任务、任意路径）、UAC 提权顺畅、用户熟悉、SmartScreen 只要签名 + 下载信誉就基本顺畅；弊 = 卸载可能留残留（靠 Inno 卸载逻辑控制）、无内置自动更新（要自建 update checker）。
  - **结论**：本产品需要管理员服务 + 进程控制 → **经典安装器（Inno Setup 或 WiX）为主**，MSIX 仅作为未来可选（如监控版轻量分发）。
- **签名预算（个人开发者低成本方案）**：
  - 免费：自签名（New-SelfSignedCertificate）→ SmartScreen 拦"未知发布者"，只适合内测/旁加载。
  - **Azure Trusted Signing**（微软云签名，推荐）：免硬件 token（传统 EV 必须硬件 key），价格约 $9.99/月基础档（按签名次数计费，比传统 OV 便宜一个量级），信任度 OV 级（无 SmartScreen 即时信誉，但比自签名强），与 GitHub Actions 集成好。参考：[KeyQ 的 Azure Trusted Signing 端到端指南](https://www.keyq.cloud/blog/windows-code-signing-with-azure-trusted-signing/)
  - 传统 OV：国际 DigiCert/Sectigo ~$200-400/年；2026 年国行涨价后 OV ~¥3588/年、EV ~¥6888/年（[参考](https://www.163.com/dy/article/KKUHQG430518HLI8.html)）——EV 贵且需硬件 token，个人开发不划算。
  - SmartScreen 信誉本质是"下载量 + 时间"积累，EV 只是跳过积累期；对新产品先用便宜 OV / Azure Trusted Signing 攒信誉即可。

### 4.3 仍待确认（不阻塞 M1 骨架，M1 内定稿）

- 通信机制最终选型（gRPC over named pipes vs 裸 pipes + JSON）——已推荐，M1 骨架时定
- 服务身份最终形态（LocalSystem vs 独立低权服务账户 + 按需提权）——已推荐 LocalSystem 起步
- 签名方案（Azure Trusted Signing vs 传统 OV）——M4 前定，内测期自签名

### 4.4 下一步
- [x] 更新 SPEC v0.2 + 本记录（本会话）
- [ ] 用户确认后开工 M1：core+service 骨架 + CI + WinUI 3 壳 + 遥测采集（schema 定稿）

---

## 2026-08-15 会话 ⑤ — 最终拍板（8 项决策确认）

**背景**：用户对会话④的推荐逐项确认，并追问前台检测归属（GUI vs service）。

### 5.1 最终拍板（已全部同步 SPEC v0.2）

| # | 待定项 | 最终决策 |
|---|---|---|
| 1 | 程序结构 | **core + service**（确认） |
| 2 | 遥测 schema | 按推荐来：M1 定义每个事件字段（事件类型、字段名、类型、单位、时间戳），SQLite 表结构 = schema 落盘 |
| 3 | 降优手段 | 优先级 + 亲和性硬降（确认）+ **必须恢复原值**：记录原优先级类/原亲和性掩码，条件解除/超时/引擎退出时自动恢复，恢复动作入决策日志 |
| 4 | 前台检测归属 | **必须在用户会话内做**（接口限制，见 5.2）；GUI 侧 SetWinEventHook 事件驱动 → 管道上报服务 |
| 5 | 分发渠道 | **经典安装器**（Inno Setup / WiX，确认） |
| 6 | 签名方案 | 开发期**自签名**；发布版 **Azure Trusted Signing**（平价云签名） |
| 7 | 通信机制 | **gRPC over named pipes**（确认） |
| 8 | 服务身份 | LocalSystem 起步（确认）+ **杀毒误报对策**：只调文档化 API（Lasso/BES 同款行为）、发布版签名、不注入不 hook、误报走厂商申诉 |

### 5.2 前台检测的技术论证（用户追问：GUI 退后台还有能力吗？service 是不是更合适？）

**结论：前台检测不能放 service——接口限制决定只能放用户会话侧。**

- **接口限制**：Windows 服务默认跑在 Session 0（非交互会话），用户桌面在 Session 1+。`GetForegroundWindow()` 只返回**调用进程所在 desktop** 的前台窗口，Session 0 服务调用拿不到用户桌面前台；`SetWinEventHook` 的 hook 也是**会话内**有效的，服务收不到用户会话的前台切换事件。→ 这不是"选哪边更合适"的问题，是 API 只允许在用户会话侧做。
- **GUI 退后台还有能力吗？有。** GUI 最小化/退托盘只是隐藏窗口，**进程仍在用户会话内运行**，hook 照常触发、检测照常工作。唯一丢失场景 = GUI 进程完全退出 → 此时启发式降级为"无前台信息"保守模式（不主动降后台进程，避免误伤），安全性不损。
- **安全性**：前台检测只需普通权限，GUI 做反而更安全（无需提权）。响应性：事件驱动 + 管道亚毫秒级，比 service 轮询更快更省电。
- **M2 增强**（GUI 完全退出也不丢）：服务用 `WTSQueryUserToken` + `CreateProcessAsUser` 在用户会话派生**无头 helper 进程**做检测，管道协议把 helper 当普通客户端即可，架构无需改动。

### 5.3 下一步
- [x] 更新 SPEC v0.2 + 本记录（本会话）
- [ ] **开工 M1**：core+service 骨架 + CI + WinUI 3 壳 + 遥测采集（schema 定稿）——用户确认后启动

---

## 2026-08-15 会话 ⑥ — Windows 版本支持决策（补充待定项）

**背景**：用户追问 WinUI 3 是否同时支持 Win10/Win11、UI 样式是否一致。

**结论（已同步 SPEC v0.2 §6 选型处）**：
- **支持范围**：Windows 10 1809+ 与 Windows 11（Windows App SDK 最低要求）
- **样式一致性**：WinUI 3 控件 XAML 自绘制、不依赖系统主题 → 控件级 Win10/Win11 一致，**单套 UI，无需两套设计**；系统级差异（窗口圆角 / Mica / 标题栏 / 系统对话框 / 字体）运行时版本检测 + 条件资源适配，Mica 等 Win11-only 特效在 Win10 降级
- **时效性背景**：微软 2026-06 宣布 Win10 免费安全更新延长至 2027-10-12（原定 2025-10 终止）——Win10 仍活跃，大量升不了 Win11 的老机器正是"觉得电脑卡"的目标用户，支持 Win10 对"抢 Lasso 用户盘"定位是利好
- **对 M1 的影响**：CI 矩阵覆盖 Win10 与 Win11

### 6.1 下一步
- [x] 更新 SPEC v0.2 + 本记录（本会话）
- [ ] **开工 M1**：core+service 骨架 + CI（Win10/Win11 矩阵）+ WinUI 3 壳 + 遥测采集（schema 定稿）——用户确认后启动

---

## 2026-08-15 会话 ⑦ — M1 开工：骨架 + schema 定稿 + 遥测采集（已完成）

**背景**：用户运行 `/winui-setup` 补齐环境后确认开工 M1。

### 7.1 环境修复（/winui-setup 记录，未来会话必读）

- **winget 在本机 DSH 会话不可直接运行**：DSH 文件沙箱拦截 winget 对 `%LOCALAPPDATA%\Packages\...` 的写入（报 `0x8A150001` 无输出）。解法：DSH 内跑 winget/dotnet build/test 等需要写工作区外 + 联网的命令，一律用 `danger-full-access` 权限。
- **技能脚本包 ID 修正**：`Microsoft.WinAppCLI`（技能原文）在源中不存在，实际是 **`Microsoft.WinAppCli`**（大小写敏感）。
- **NuGet 认证失败根因**：受限沙箱阻断 SSPI 凭据访问（"安全包中没有可用的凭证"），完整权限下正常。
- **UE dotnet PATH 劫持**：系统级 PATH 顺序正确（`C:\Program Files\dotnet` 在 UnrealEngine 之前），DSH 会话内需手动刷新 PATH：`$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`。
- 安装结果：winapp 0.6.0 ✅、WinUI 模板 ✅（winui-mvvm 等）、.NET SDK 8.0.424 + 9.0.306 ✅。

### 7.2 M1 已交付

| 交付物 | 位置 | 说明 |
|---|---|---|
| 解决方案 | `Cpo.sln` | core + interop + service + tests + app 五工程 |
| 遥测 schema v1.0 | `docs/schema.md` | **M1 验收点①**：7 类事件字段定稿 + SQLite 落盘形态 |
| core | `core/Cpo.Core/` | 事件模型、`TelemetryEventSerializer`（camelCase payload，type 独立列）、`ITelemetryStore`/`SqliteTelemetryStore`、`SamplingConfig`、纯逻辑 `CpuUsageCalculator`/`ProcessLifecycleDetector` |
| interop | `interop/Cpo.Interop/` | `ProcessSampler`（Toolhelp32 + GetProcessTimes/GetProcessMemoryInfo/QueryFullProcessImageNameW）、`SystemSampler`（GetSystemTimes/GlobalMemoryStatusEx）。全部 DllImport（不用 LibraryImport——ByValTStr/FILETIME 封送支持差），`CharSet.Unicode` 必须显式声明 |
| service | `service/Cpo.Service/` | `TelemetryRecorder`（周期采集 → 事件 → SQLite）+ 控制台宿主（`--interval-ms`/`--retention-days`/`CPO_DB_PATH` 覆盖） |
| tests | `tests/Cpo.Tests/` | **35 个 xUnit 全通过**：CPU 计算、生命周期 diff、事件序列化契约、SQLite 存储（内存共享库） |
| app | `app/Cpo.App/` | WinUI 3 壳（M1 直读 SQLite 展示事件流，M2 换 gRPC）。打包运行验证通过 |
| CI | `.github/workflows/ci.yml` | 编译 + 单测强制，Win10（windows-2022）/Win11（windows-latest）矩阵 |

### 7.3 关键技术决策与坑（M2 必读）

- **事件序列化**：不用 System.Text.Json 多态判别符——net8 下 `[JsonIgnore]` 在抽象属性上不生效、且 `Type` 属性与判别符同名冲突导致反序列化崩溃。改为 `TelemetryEventSerializer`：payload = camelCase 业务字段（无 type），`type` 独立列落盘，反序列化按 type 手动 dispatch。
- **枚举序列化**：加 `JsonStringEnumConverter(CamelCase)`，schema 枚举值落盘为字符串（`"started"` 而非 `0`）。
- **SQLite 内存库**：`:memory:` 每连接独立库，测试必须用 `file:xxx?mode=memory&cache=shared`（`CreateInMemory()` 已封装，每实例 GUID 唯一）。
- **SQLite 查询**：进程过滤用 `json_extract(payload,'$.pid')`（payload 是 camelCase，注意大小写）。
- **进程采样容错**：svchost 等系统进程普通权限打不开句柄，`Capture` 内所有子步骤单独 try/catch，单进程失败不丢整条快照（path/内存按 null/0 处理，M2 服务以管理员运行后可拿全）。
- **MVVM 模板默认 C# 13 partial property**（CommunityToolkit.Mvvm 8.4）——net8 SDK 只支持 C# 12，改回字段语法 `[ObservableProperty] private string _x;`（会带 MVVMTK0045 AOT 提示警告，M1 可接受，M3 前评估升级 LangVersion）。
- **WinUI 构建平台**：sln 默认 Any CPU 对 WinUI 项目无效，必须 `-p:Platform=x64`；winapp run 时不能加 `--no-build`（打包布局需要重新生成 AppxManifest）。

### 7.4 验收结果（M1 验收标准全部达成）

- ✅ **能录制本机负载轨迹**：service 运行 6 秒 → SQLite 写入 2804 条真实事件（sample.cpu 1309 + sample.memory 1309 + process.lifecycle 186），时间范围回放、PID 过滤查询均验证通过。
- ✅ **schema 定稿**：`docs/schema.md` v1.0，与 core 模型/落盘/回放一致。
- ✅ **编译 + 单测强制**：`dotnet build` 0 错误；`dotnet test` 35/35 通过；CI workflow 就位。
- ✅ **WinUI 3 壳**：winapp run 打包启动成功（PID 验证、窗口响应正常）。

### 7.5 下一步（M2）
- [ ] 回放框架 + 决策日志（`policy.decision`/`policy.action`/`rule.changed` 事件接入）
- [ ] 基础策略（显式规则优先）+ 前台检测（GUI 侧 SetWinEventHook → 管道上报）
- [ ] GUI↔服务通信：gRPC over named pipes（M1 已定案）
- [ ] service 转 Windows 服务形态（LocalSystem）

---

## 2026-08-15 会话 ⑧ — M1 收尾修复（打包虚拟化 + UI 布局）

**背景**：用户实机打开 app 验证，发现两处问题并已修复。

### 8.1 打包应用 LocalAppData 虚拟化（M2 必读，直读 SQLite 的硬伤）

- **现象**：app（MSIX 打包）打开后报"加载失败"。
- **根因**：打包应用的 `Environment.SpecialFolder.LocalApplicationData` 被虚拟化重定向到
  `%LOCALAPPDATA%\Packages\<PFN>\LocalCache\Local`，与 service（普通进程）写入的真实用户目录
  `%LOCALAPPDATA%\Cpo\telemetry.db` **不是同一个路径**。加上 app 侧漏了 `Directory.CreateDirectory`（service 有），首次运行直接异常。
- **处理**：app 侧补建目录 + 空库/异常友好提示。
- **对 M2 的启示**：直读 SQLite 在打包/非打包混合架构下路径天然不一致，**必须按 M1 定案切 gRPC over named pipes**（服务推送，app 不碰文件）。M2 实现通信前，本地演示可用 `CPO_DB_PATH` 环境变量 + 把库录到 app 虚拟路径。

### 8.2 UI 布局坑：DataTemplate 内 Grid 列未指定（XAML 常识但易犯）

- **现象**：事件流每一行文字全部叠在一起。
- **根因**：`DataTemplate` 内 `Grid` 定义了三列，但三个 `TextBlock` 都没写 `Grid.Column`，默认全落在第 0 列。
- **处理**：显式 `Grid.Column="0/1/2"` + `VerticalAlignment="Center"` + 长文本 `TextTrimming="CharacterEllipsis"`（摘要列 `MaxLines="1"`）。

### 8.3 状态
- [x] 修复并重新部署验证（用户确认布局正常）
- [ ] M2 开工

---

## 2026-08-15 会话 ⑨ — M2：策略引擎 + 回放框架 + 决策日志（已完成）

**背景**：用户确认 M1 后开工 M2（SPEC §8：回放框架 + 决策日志 + 基础策略，验收=能离线回放评估策略、优先级/亲和性可用且可恢复）。

### 9.1 M2 已交付

| 模块 | 位置 | 说明 |
|---|---|---|
| 规则模型 | `core/Rules/PolicyRule.cs` + `RuleMatcher.cs` | 进程名通配符（\* / ?），SetPriority/SetAffinity/SetBoth，DurationMs，IsEnabled |
| 策略引擎 | `core/Engine/PolicyEngine.cs` | **纯逻辑决策函数**：规则优先匹配 + 前台进程保护；回放与线上共用 |
| 确定性屏障 | `core/Engine/ProposalBus.cs` + `ExecutionPath.cs` | 引擎只产建议（ProposalBus），执行只经 ExecutionPath；AI 未来只走建议通道（SPEC 铁律） |
| 执行路径 | `interop/ProcessController.cs` | SetPriorityClass/SetProcessAffinityMask P/Invoke + 原值记录；`IProcessController` 抽象使 core 零 Win32 依赖 |
| 干预恢复 | `ExecutionPath` | 记录原值；条件解除/超时（DurationMs）/引擎退出（RestoreAll）自动恢复；恢复动作入决策日志 |
| 回放框架 | `core/Replay/ReplayRunner.cs` | SQLite 事件流 → 重建帧（进程/系统 CPU）→ 逐帧引擎评估 → 汇总 |
| 规则存储 | `core/Engine/RuleStore.cs` | JSON 文件持久化 + rule.changed 事件（drain 一次性消费） |
| 决策日志 | `core/Engine/DecisionLogger.cs` | ProposalBus/ExecutionPath/RuleStore → policy.decision/action/rule.changed 事件 |
| 策略运行器 | `service/PolicyRunner.cs` | 滑动窗口查询 → 引擎 → 监督（只建议）/自动（执行） |
| 宿主参数 | `service/Program.cs` | `--engine=auto|supervised`、`--rules=<json>`、内置演示规则（\*build\* 降 BelowNormal） |
| 工具 | `tools/ReplayDemo/` | 真实轨迹离线回放演示（M2 验收） |
| 测试 | tests | **83 个 xUnit 全通过**（规则匹配/引擎/执行恢复/回放/日志） |

### 9.2 关键设计与坑（M3 必读）

1. **滑动窗口而非增量窗口**：PolicyRunner 最初用 `_lastSampleMs` 增量查询（from=上次评估时刻），结果**永远查不到刚采的数据**——采样落库有滞后（`SnapshotAll` 枚举 300+ 进程耗时数百 ms，事件 ts 是采样时刻早于评估时刻）。改为**固定 5s 滑动窗口 + 每进程取最新样本**（`latestTsByPid` 比较）。
2. **冷却期防抖**：超时恢复后下轮评估会立即重新干预（抖动循环）。ExecutionPath 增加 `_lastRestoredMs`：恢复后 DurationMs 内同建议返回 "cooldown"。**恢复时刻必须由调用方传入**（ReapExpired 的 nowMs / Restore 的 UtcNow），否则测试用模拟时间戳时冷却永不结束（`_lastRestoredMs` 被写成真实大数）。
3. **幂等留痕**：干预期内重复建议返回 "already active"（不重复执行但**仍写 decision 日志**）；`ExecutionEvent.Error` 区分 `null`(执行)/`already active`(幂等)/`cooldown`(冷却)——决策日志可完整还原干预生命周期。
4. **枚举序列化**：RuleStore JSON 必须配 `JsonStringEnumConverter(CamelCase)`，否则 `"action":"setPriority"` 反序列化抛 JsonException（service 启动即崩，注意区分启动崩溃与运行异常）。
5. **interop 依赖 core**：ProcessController 用了 core 的 `IProcessController`/`ProcessControlState` → Cpo.Interop.csproj 加 core 引用（M1 时 interop 无依赖）。
6. **`RuleChangeSource` 命名空间重复**：Telemetry 与 Rules 都有定义 → 删 Rules 版，统一用 Telemetry 的。
7. **ExecutionEvent 时间**：`ToActionEvent` 用 `ExecutedMs`（动作时刻）而非 `Proposal.TsMs`（建议时刻），测试曾因 20000 vs 12345 暴露。

### 9.3 M2 验收结果（全部达成）

- ✅ **离线回放**：真实轨迹 4207 事件 → ReplayRunner 重建 6 帧 → `*build*` 规则 0 建议（压力进程是 powershell 不匹配，正确）/ `*` 规则 1928 条建议。
- ✅ **优先级/亲和性可用且可恢复**：实机验证优先级 0x20→0x4000、亲和 0xFFFF→0x3、恢复后完全匹配原值。
- ✅ **全生命周期闭环**（决策日志还原）：SetPriority 成功 → already active×3 → **Restore 成功**（3s 超时）→ cooldown×2 → 重新干预 → already active×2。policy.decision:10 / policy.action:11 / rule.changed 落盘。
- ✅ **编译 + 单测强制**：build 0 错误，83/83 通过。

### 9.4 下一步（M3）
- [ ] 启发式 v1（监督模式）+ 现代化 UI 完整化（引擎建议 → 用户采纳闭环）
- [ ] 前台检测（GUI 侧 SetWinEventHook → 管道上报，M2 增强：WTSQueryUserToken helper）
- [ ] GUI↔服务通信：gRPC over named pipes
- [ ] service 转 Windows 服务形态（LocalSystem）+ 杀软误报对策验证

---

## 2026-08-15 会话 ⑩ — M2 收尾修复：事件列表显示最近而非最早 100 条

**背景**：用户实机验证 M2 时发现 app 事件流显示的是最早 100 条而非最近 100 条。

### 10.1 根因与修复

- **根因**：`EventQuery` 的 SQL 固定 `ORDER BY ts_ms ASC LIMIT N`（升序 = 最早在前），app 直接取前 100 条 → 显示最早事件。
- **修复**：`EventQuery` 新增 `Descending` 选项（`ORDER BY ts_ms DESC`）；app 用 `Limit=100, Descending=true` 取最近 100 条 → `Reverse()` 后展示（时间从上到下递增，最新在底部，与回放语义一致）。
- **测试**：新增 2 个（倒序返回最新优先、倒序 + 类型过滤），85/85 通过。

### 10.2 验证

- ✅ 用户实机确认：任务管理器看到压力进程优先级 BelowNormal ↔ Normal 周期切换（规则 5s 超时 + 冷却），功能正常
- ✅ app 列表显示几秒前的最新事件（policy.decision/action 出现在列表底部）

### 10.3 备注（M3 前）

- 验证期间 db 曾积累 337 万条事件（多次重启 service + 1s 采样间隔 + 300+ 进程）——本地演示前先清库，或调大采样间隔。默认 30 天保留策略 + 1h 清理周期足够生产，但本地反复验证时库会膨胀。

---

## 2026-08-15 会话 ⑪ — CI 触发策略调整（重要：日常 push 不再触发）

**背景**：用户指出每次 push 都触发 GitHub CI 没必要（当前 workflow 还有报错没跑通，想之后修）。

### 11.1 决策（已同步 SPEC §12）

**CI 只在「打 tag」或「手动触发」时运行，日常 push / PR 不触发。**

`.github/workflows/ci.yml` 的 `on` 改为：

```yaml
on:
  push:
    tags:
      - 'v*'         # 打 tag（如 v0.1.0）触发
  workflow_dispatch:  # GitHub Actions 页面手动 "Run workflow"
```

### 11.2 触发方式速查（未来会话提醒自己）

| 想触发 CI 时 | 命令 / 操作 |
|---|---|
| 里程碑/发布节点 | `git tag v0.x.x && git push --tags`（注意：`git push` 不会带 tag，必须 `--tags`） |
| 按需验证 | GitHub 网页 → Actions → CI → **Run workflow** 按钮（下拉可选手动触发） |
| 日常提交 | 什么都不用做——push 不触发 CI |

### 11.3 待办（用户明确"之后再搞"）

- **CI workflow 目前有报错没跑通**（M2 期间数次 push 的 Actions 运行失败）。已知嫌疑：windows-latest 上 WinUI 构建（`-p:Platform=x64` 与 sln 平台映射）或测试输出路径；**M3 阶段修复后再打 tag 验证**。修复前不要依赖 CI 结果。
- 相关：SPEC §12 铁律"每个 PR 必须过 CI"已按新策略表述（见 SPEC 更新）；**核心质量门禁 = 本地 `dotnet build` + `dotnet test`（当前 85/85 通过）**，CI 是发布节点兜底。

---

## 2026-08-15 会话 ⑫ — **重大产品决策：废除"监督模式逐条采纳"，改自动执行 + 全量留痕 + 审阅 + 开关**

**背景**：规划 M3 监督模式闭环（建议推送 → 用户一键采纳）时，用户拍板推翻该方向。

### 12.1 决策（已同步 SPEC §4 铁律 2 + §5 v1）

> **引擎默认自动执行，不做逐条审批；每次干预全量留痕（做什么/为什么/持续多久/恢复没）；用户定期打开 app 审阅操作日志，并可用全局开关控制引擎启停。**

用户理由：逐条采纳太慢、不够及时（卡顿是实时事件，用户不会守在 app 前）；Process Lasso 的 ProBalance 也是自动干预模式。

### 12.2 对架构的影响（M3 必读）

- **不要做**：建议推送 → 采纳/拒绝 → 执行的逐条审批链路（gRPC 建议流、SuggestionStore 状态机、采纳 UI 均**取消**）
- **要做**：
  1. **审阅面板**（app）：决策日志可视化（时间线/列表：干预了谁、为什么、参数、是否已恢复）——信任工具 + 营销素材（SPEC §5 已有）
  2. **ProBalance 式全局开关**（app → service）：**只控制干预执行**。**用户澄清（12.2 补充）：开关 ≠ 服务开关——关闭时遥测采集、决策日志照常，service 照常驻**；引擎仍评估并记录 decision（`policy.decision`），只是不执行 action（类似 M2 的 supervised 模式语义 = "记录但不执行"）
  3. 自动执行链路（**M2 已全部就绪**：PolicyRunner 自动模式 + ExecutionPath 留痕/恢复/冷却——只需把监督模式从"默认"改为"可选/开关态"）
  4. 启发式 v1（M3 核心，自动执行 + **保守参数起步**，配合审阅面板建立信任）
- **风险提示**（已告知用户，接受）：自动执行对"零误伤口碑"（SPEC §11）压力更大 → 启发式必须保守起步 + 前台保护 + 完整恢复，审阅面板是信任兜底
- **通信需求变化**：不再需要 gRPC 建议流；仍需要**控制面**（开关下发）+ **审阅数据面**（日志/状态查询）。M3 评估：先直读 SQLite（审阅，M1 模式）还是直接上 gRPC named pipes（定案方向）

### 12.3 状态
- [x] SPEC §4/§5 已同步（2026-08-15）
- [x] M3 按新方向规划开工（会话⑬：数据管理 + gRPC + 审阅 UI）

---

## 2026-08-15 会话 ⑬ — M3 第一阶段：双表数据分层 + gRPC over named pipes + 操作日志审阅面板

**背景**：用户拍板 ① 数据分层双表（高频采样短 Buffer + 低频日志长保留）② 提前接 gRPC ③ 操作日志动态刷新（最新在最上）。

### 13.1 双表数据分层（schema v1.1，docs/schema.md §8）

| 表 | 事件 | 保留 | 用途 |
|---|---|---|---|
| `samples`（热） | sample.cpu / sample.memory | 1 小时（默认） | 决策输入（5s 滑动窗口）+ 近期诊断 |
| `event_log`（冷） | lifecycle / policy.* / rule.changed / ui.foreground | 30 天（默认） | 审阅/长期诊断/AI 语料 |

- 路由：`TelemetryTableRouter`（core/Storage，单一事实来源，测试覆盖 7 类事件归属）
- 清理：service `PurgeLoopAsync` 每 PurgeIntervalMs（默认 1h）分级 DELETE（**M2 遗留缺陷：PurgeBeforeAsync 从未被调用，库膨胀到 337 万条的根因——现已接入**）
- 采样默认回 2s（SPEC 默认）
- 实测分布：samples 30006 vs event_log 464（98.5% vs 1.5%），数据量问题根治

### 13.2 gRPC over named pipes（定案落地）

- **契约**：`contracts/Cpo.Contracts/telemetry.proto`（新工程，service+app 共用，GrpcServices=Both）
  - `QueryEvents`（类型/前缀/PID/时间范围/分页/倒序）
  - `WatchEvents`（服务端流，M3 简化=轮询拉取，后续可升级内存广播）
  - `GetStatus`（引擎模式/双表计数）
- **事件信封**：`{ ts_ms, type, payload_json }`——payload_json 直接复用 schema JSON 契约（TelemetryEventSerializer），**gRPC 是传输层不改存储格式**（M3 定案）
- **服务端**：Grpc.AspNetCore + `ListenNamedPipe("cpo-telemetry-<user>")`（Kestrel Http2）
- **客户端**：`GrpcChannel.ForAddress("http://localhost")` + `SocketsHttpHandler.ConnectCallback` 返回 `NamedPipeClientStream`（官方模式，见[微软文档](https://learn.microsoft.com/aspnet/core/grpc/interprocess-namedpipes)）
- 管道名含用户名避免多用户冲突

### 13.3 app 审阅面板（M3 UI 第一版）

- MainPage 改为「操作日志」：只查 `policy.` 前缀事件，**最新在最上面**（Descending + Insert(0)）
- **动态刷新**：每 2s 轮询（PeriodicTimer），新事件插顶部，截断 200 条
- 摘要人性化：`降优先级: powershell → 成功（already active）` / `恢复原值: ...` / `规则 xxx: added`
- 数据源 gRPC，**根治 M1 打包虚拟化路径问题**（app 不再直读 SQLite）
- 状态卡片显示引擎模式 + 双表计数

### 13.4 坑与决策（M3 后续必读）

1. **gRPC 生成类命名冲突**：proto `service TelemetryService` 生成静态类 `TelemetryService`，实现类不能同名 → 实现类名 `TelemetryGrpcService`
2. **CreateSlimBuilder 顺序**：必须 `builder.Services.AddSingleton` 后再 `builder.Build()`；`WebApplication.Services` 是 IServiceProvider 只读
3. **named pipe 客户端没有 TransportType 选项**：官方做法是 SocketsHttpHandler.ConnectCallback 返回 NamedPipeClientStream（容易踩的坑）
4. **测试并行冲突**：SqliteTelemetryStore.DisposeAsync 的 `ClearAllPools` 是全局的，会杀掉其他测试类的共享内存库（`no such table: event_log`）→ 两个测试类加 `[Collection("NonParallelGrpc")]` 串行
5. **UNION 查询不能直接 ORDER BY ts_ms**：UNION 结果集无该列 → 每支子查询带 ts_ms，外层包装 `(SELECT type,payload,ts_ms ... UNION ALL ...) ORDER BY ts_ms`
6. **双表 CountAsync 签名变化**：`CountAsync(TelemetryTable)` / `PurgeBeforeAsync(TelemetryTable, ts)`，旧单参版本已删
7. **EventQuery 增加 TypePrefix**（"policy." 前缀）与 Table 显式指定，审阅面板按前缀查询

### 13.5 验收结果

- ✅ 95/95 单测全绿（新增：双表路由/前缀查询/显式表/分级清理/gRPC 管道 4 项集成 + 认证拒绝）
- ✅ 实机端到端：service（gRPC 管道）→ 压力进程降优 → app 显示「已连接 · 最近 20 条操作记录（每 2s 自动刷新）」+ 干预记录实时出现
- ✅ 双表计数验证：samples 30006 / event_log 464（数据分层生效）
- ✅ 库大小问题根治（清理循环接入 + 采样回 2s + 分层保留）

### 13.5b gRPC 安全加固（用户追问"任意进程都能访问吗"后实施）

**问题**：`ListenNamedPipe` 默认 ACL 允许**同用户任意进程**连接（其他用户被拒），无身份校验。

**加固（两层，已实机验证）**：
1. **管道 ACL**：`WebHost.UseNamedPipes(o => o.CurrentUserOnly = true)`——Kestrel 自动把管道 ACL 限制为当前用户（防其他用户/服务）
2. **连接令牌**：service 启动时生成 256-bit 随机令牌写 `%PROGRAMDATA%\Cpo\auth-token`（ACL 限 SYSTEM+当前用户）；gRPC 所有 RPC 经 `AuthInterceptor` 校验 metadata `cpo-auth-token`（恒定时间比较防时序侧信道）；app 读同一文件携带令牌
- 实测：无令牌/错误令牌 → `Unauthenticated` 被拒；正确令牌 → 正常
- **威胁模型说明**：令牌文件同用户可读（app 需要），防的是"同用户任意进程直接调用"；同用户恶意进程仍可读令牌文件——真正强校验（对端 PID 验证）需要自定义传输层，M3 评估，当前基线对齐 Process Lasso 级别
- 坑：`FileSecurity` 在 `System.IO.FileSystem.AccessControl` 包，命名空间 `System.Security.AccessControl`；`File.SetAccessControl` 用 `FileInfo.SetAccessControl` 替代（2 参重载解析问题）

### 13.6 下一步（M3 续）
- [ ] ProBalance 开关（app → service 控制面，gRPC SetInterventionEnabled，运行时切换引擎态）
- [ ] 启发式 v1（CPU 风暴检测，保守参数起步）
- [ ] 审阅面板增强（按进程过滤/时间线/恢复状态可视化）
- [ ] WatchEvents 升级为真实流推送（引擎评估时广播，去轮询）

## 2026-08-15 会话 ⑭ — UI 修复（自动刷新+排序）+ 服务生命周期与安全级别定案

**背景**：修复操作日志"最新不在最上 / 不自动刷新"（Q2）；用户拍板 ① Service 生命周期方案 ② 安全级别到对端进程校验为止。

### 14.1 Q2 修复：列表自动刷新 + 最新在最上（已实机验证）

- **根因**：① Descending 查询结果被 `Insert(0)` 倒插（最新反而跑到底）② PeriodicTimer 后台任务直接改 ObservableCollection（跨线程，UI 不刷新）
- **修复**（`app/Cpo.App/ViewModels/MainPageViewModel.cs`）：
  - 轮询改为**整表重建**：按 gRPC 响应顺序赋值（Events[0] = 最新），不再 Insert(0)
  - 捕获 UI DispatcherQueue：集合与 StatusText 全部走 `_uiDispatcher.TryEnqueue`（后台线程只读数据，UI 线程落集合）
  - 每次轮询独立 try/catch（瞬断不杀轮询任务）；状态卡显示"最后刷新 hh:mm:ss"便于肉眼验证
- **验证**：刷新时间戳 21:48:11 → 21:48:21 滚动（2s×2 周期）；列表顶部为最新策略事件（21:47:31 → 21:41:55 → 21:41:53 递减）
- 备注：日志中偶见 "process not found" = 进程已退出但引擎仍在评估残留样本，属正常行为

### 14.2 决策 ①：Service 生命周期 = Windows 服务 + 开机自启 + App 拉起 + 托盘

用户拍板（行业常规，对标 Process Lasso）：
1. **service 注册为 Windows 服务**（LocalSystem，**开机自动启动**）——M4 安装器（Inno Setup）注册；服务与 App 是否打开无关，引擎全天候常驻
2. **App 启动探测 + 拉起**：启动时经 gRPC `GetStatus` 探测，未运行则经 SCM 启动 service（开发态先直接启动进程）
3. **App 支持最小化到托盘**：关窗收托盘不退出，进程仍在用户会话 → 前台检测 hook 照常工作（复用会话⑤结论）
- 已同步 SPEC §6「服务生命周期」bullet + §8 M4 验收标准

### 14.3 决策 ②：安全级别定案 = 令牌 + 对端进程校验（不做会话令牌）

- **威胁模型回顾**：令牌文件同用户可读（app 需要），同用户恶意进程理论上可读令牌 → 模拟出合法调用
- **用户拍板：纵深防御做到"对端进程校验"为止**——named pipe `GetNamedPipeClientProcessId` 拿调用方 PID → 校验必须是 Cpo.App.exe（进程路径/签名）；**即使令牌泄露，别的进程也调不动 gRPC**
- **明确不做**：会话令牌（短期轮换握手）——成本高收益低，到此为止
- **实现现状**：Kestrel 不暴露对端 PID → 需自定义传输层（自管 named pipe）或降级方案，**M3 内评估实现路径**；当前基线（CurrentUserOnly + 令牌，95/95 测试含认证拒绝）保持有效
- 已同步 SPEC §6「gRPC 安全基线」bullet + §8 M3 验收标准

### 14.4 下一步（M3 续）
- [ ] **对端进程校验**：评估自定义传输层（named pipe + Http2 帧）vs Kestrel 扩展，选定后实现
- [ ] 其余 M3 续项同 13.6（ProBalance 开关 / 启发式 v1 / 审阅面板增强 / WatchEvents 推送）

## 2026-08-15 会话 ⑮ — 列表闪烁修复（增量合并）+ 自动重连 + 规则进程名坑

**背景**：用户反馈 ① 压力进程不在了、日志没更新 ② 操作日志列表一直在闪。

### 15.1 日志不更新的原因（两点叠加）

1. **压力进程早已清理**（会话⑬演示完杀掉）；当前系统安静 → 无新 policy 事件（`sample.*` 不进操作日志列表，只有状态卡计数在走）
2. **规则进程名匹配坑（真 bug）**：`Process.ProcessName` **不含 .exe 后缀**，而演示规则文件写的是 `"powershell.exe"` → 永远匹配不上 → 引擎零干预。修复：`tools/demo-rules.json` 改为 `"powershell"`（新建，供演示复用）；顺带修正 `PolicyRule.cs` 注释里 `msbuild.exe` 的误导示例
3. 验证：压力进程（2× powershell 死循环）→ `policy.decision` + `policy.action(setPriority)` 实时落库，列表恢复更新

### 15.2 列表闪烁根因与修复（增量合并）

- **根因**：每 2s 轮询 `Events.Clear()` + 全量重填 → 即使内容没变也触发整表重绘（闪烁、滚动位置丢失）
- **修复**（`app/Cpo.App/ViewModels/MainPageViewModel.cs`）：`MergeRows` 增量合并——
  1. 快速路径：与现有列表逐项比 Key（`ts|type|summary`，事件不可变 → Key 稳定），完全一致 → **零操作**（不触发任何 UI 重绘）
  2. 有差异：只删"被挤出 Limit 上限"的尾部项、只在缺失处插入（最新在前）
- **注意**：Key 依赖 ts|type|summary 三元组，同毫秒同类型同内容的重复事件会被视为同一行（实际不可能出现，可接受）

### 15.3 附加修复：App 断线自动重连（实测验证）

- **问题**：app 在 service 停止期间启动 → `StartAsync` 首连失败直接 return → 轮询循环从未启动，永久卡"连接失败"
- **修复**：首连失败不退出，进入轮询循环，每 2s 重试（per-poll try/catch 已有）
- **验证**：service 重启后 app 未重启，日志中自动恢复 QueryEvents 轮询（新日志 24 次调用）——呼应决策①"App 探测 + 拉起"的韧性要求

### 15.4 验收
- ✅ 95/95 测试全绿（本轮为 app/core 注释/工具文件改动，无测试影响面）
- ✅ 实机：压力进程被降优（decision+action 落库）→ app 增量更新无闪烁；service 重启 app 自动重连

## 2026-08-15 会话 ⑯ — 增量更新残留问题：视口内行"消失"（排序确定性 + 虚拟化）

**背景**：用户反馈不闪了，但刷新时视口内部分行短暂消失——置顶时下半视口消失、滚到底时上半视口消失。

### 16.1 根因（两层，都修了）

1. **排序非确定性（主因）**：`ORDER BY ts_ms DESC` 无次级键。一轮策略评估的 decision+action 常**同一毫秒**落库（同 ts 多条），`LIMIT 20` 的 top-N 排序随数据插入变化 → 同 ts 事件顺序在轮询间**翻转** → 增量合并把视口内行删掉重插（表现为消失/重现，位置随机）
   - 修复（`SqliteTelemetryStore.BuildSelect`）：单表排序加次级键 `id`（自增=插入序）；跨表 UNION 用 `type` 破平（类型按表路由，同 ts 同 type 不可能跨表 → 全序确定）
   - 新测试 2 个：`Query_Descending_TieBreak_IsStableByInsertionOrder` / `Query_Union_TieBreak_IsDeterministic`
2. **虚拟化容器回收**：ListView 默认 ItemsStackPanel 虚拟化，顶部插入时容器回收/重排可能造成短暂空白
   - 修复（`MainPage.xaml`）：上限 20 行 → ItemsPanel 换非虚拟化 `StackPanel`，容器常驻，插入只做布局平移

### 16.2 验收
- ✅ 97/97 测试全绿（+2 新测试）
- ✅ 实机部署：app（PID 16120）自动重连、轮询正常；等用户确认视口不再消失（压力进程已按用户要求停掉，可随时重启演示）

### 16.3 动画处理（用户连续两轮反馈后定案：全部去掉，保持简洁）

- **问题链**：① 底部行删除带系统淡出动画，与顶部插入引起的下滑重叠（观感怪）→ ② 手动入场动画（淡入+下移，ContainerContentChanging + Storyboard）尝试保留插入动画，但用户没看到效果（非虚拟化 StackPanel 下容器事件时机不可靠）→ ③ **用户拍板：入场动画也去掉，简洁优先，动画细节留到之后做**
- **终态**：`ListView.ItemContainerTransitions = 空 TransitionCollection`（禁用系统增删/重定位过渡）+ 无自定义动画——插入、删除、重定位全部瞬时
- 备注：将来做动画时用非虚拟化面板时容器事件时机需重新验证（本次手动动画未生效可能与此有关，未深究）

## 2026-08-15 会话 ⑰ — 对端进程校验落地：门卫管道 + 内存会话令牌（gRPC 安全第 3 层）

**背景**：用户拍板"安全级别到对端进程校验为止"（会话⑭），本会话完成资料调研（OWASP gRPC 速查表 / grpc 官方认证分层 / Chromium RegistrationServer 案例 / 安恒管道 PID 伪造研究）并落地实现。

### 17.1 决策演进：会话令牌从"明确不做"变为"门卫的发放物"

- 会话⑭ 曾定案"会话令牌（短期轮换）不做"——当时语境是**无 PID 校验的纯令牌轮换握手**（成本高收益低）
- 落地设计后演进：**对端进程校验（门卫）是发放入口，会话令牌是发放物**——门卫已解决"令牌被偷"问题，令牌无需轮换（12h 长期有效、不落盘），成本收益翻转 → 做
- 文件令牌（%PROGRAMDATA%\Cpo\auth-token）**整体废弃**：同用户可读，无法作为有效凭据，删除全部机制

### 17.2 实现（三层纵深闭环）

1. **管道 ACL**（已有）：`CurrentUserOnly` 防其他用户
2. **会话令牌拦截器**（改造）：`AuthInterceptor` 校验 metadata `cpo-auth-token` 必须是 `SessionTokenStore` 里的有效令牌（内存字典，256-bit 随机，默认 12h 过期惰性清理）
3. **门卫管道**（新增，`service/Cpo.Service/Security/`）：
   - `GatekeeperPipe`：raw named pipe `cpo-gate-<user>`（CurrentUserOnly），App 握手 → `GetNamedPipeClientProcessId`（新增 P/Invoke，interop 层）取对端 PID → `TrustedClientValidator` 校验（进程存活 + 可执行文件完整路径 = 开发 AppX 或发布安装目录的 Cpo.App.exe，发布版可叠签名）→ 通过写回会话令牌，拒绝写空行
   - App 侧：`EnsureSessionAsync` 握手（无令牌时）、`WithAuth` 带会话令牌、**Unauthenticated 时清令牌下一轮自动重握手**（service 重启自愈，复用车轮）

### 17.3 验证（全链路实机）

- ✅ 100/100 单测全绿（+3：可信客户端发令牌 / 不可信拒绝 / 令牌过期）
- ✅ App（Cpo.App.exe）握手：日志 `[Gate] 发放会话令牌 → pid 31156`，后续 gRPC 全部 200
- ✅ **攻击模拟**：PowerShell（同用户非 App 进程）连门卫 → 回复空行，日志 `[Gate] 拒绝未知客户端 pid=32076`；旧版文件令牌调用 → `Unauthenticated`
- 结论：即使令牌文件（已废弃）/任何磁盘文件被同用户进程读到，也拿不到会话令牌 → 调不动 gRPC；管理员级攻击在 Windows 信任边界内无解（明确不做）

### 17.4 遗留（M4）
- service 以 LocalSystem 运行时，管道 ACL 需改为"仅交互用户可连"（当前 CurrentUserOnly 在 LocalSystem 下会拒绝用户 App——与 Kestrel gRPC 管道同题，M4 服务化时一并处理）

## 2026-08-16 会话 ⑱ — ProBalance 开关落地（gRPC 控制面 + 引擎干预态切换）

**背景**：会话⑫定案的"ProBalance 式全局开关"（开关只控制干预执行；遥测/日志/service 照常）此前只有产品描述，本会话落地为完整控制面。

### 18.1 设计定案

- **组合语义**：执行干预 ⇔ `Mode == Automatic && InterventionEnabled`。`Mode` 是启动参数（`--engine`），开关是运行时状态；supervised 启动默认关（命令行显式降级时开关不覆盖）
- **关闭开关 = 立即恢复全部生效干预**（ProBalance 语义：关了就别管我的进程），恢复动作照常留痕 `policy.action`（restore）
- **新 schema 事件 `policy.intervention_toggled`**（`enabled` + `source`，当前 source 仅 `app`）：开关切换本身可审计，路由冷表 event_log（router fallback 天然覆盖，零改动）
- App 开关 = `ToggleSwitch` TwoWay 绑定 + **同步标志防死循环**：`SyncInterventionEnabled`（程序侧 GetStatus 刷新/失败回滚时置位）vs `OnIsInterventionEnabledChanged`（用户操作 → gRPC）；失败回滚 UI + Unauthenticated 自动重握手
- 返回 `ServiceStatus`（切换后立即返回最新状态，App 免二次查询）

### 18.2 验证（全链路实机）

- ✅ 106/106 单测全绿（+6：PolicyRunnerTests 4 个——关不执行/开执行/关时恢复并留痕/开留痕；GrpcNamedPipeTests 2 个——状态往返/落盘事件；新增公共 TestDoubles.cs）
- ✅ UIA 切换：`winapp ui invoke InterventionSwitch` → 开关 [on]→[off]，ServiceInfo 实时变"ProBalance: 关"，日志列表顶部出现 `ProBalance 开关: 关闭（app）`
- ✅ DB 落盘：`{"enabled":false,"source":"app","tsMs":...}`（schema JSON 契约原样）
- ✅ **干预语义实机验证**：开 → 2×隐藏 powershell 风暴 → `policy.action` +2（被干预）；关 → 再跑风暴 → `policy.action` **0 增长**（干预被阻止，遥测/决策日志继续）
- 收尾：风暴进程已杀，开关恢复 [on]（保持运行环境默认）

### 18.3 备注
- GetStatus 的 `intervention_enabled` 字段从"Mode==Automatic 的映射"改为真实开关值（此前该字段恒等于引擎模式，现为独立运行时状态）
- 遗留（M3 剩余）：启发式 v1（CPU 风暴，保守）、审阅面板增强（进程过滤/时间线/恢复状态）、WatchEvents 真推送（当前 500ms 轮询）

## 2026-08-16 会话 ⑲ — M3 续项方向定案：启发式目标重定义 + 审阅面板三区布局 + WatchEvents 抗卡顿要求

**背景**：用户审阅 M3 剩余 3 项待办后给出方向性指示（三条），本会话记录为正式决策并调研图表实现方案。

### 19.1 启发式目标重定义（响应性导向，非占用率导向）——最高优先级决策

**用户原话要点**：启发式的目标 = **保持操作系统、前台程序的高响应性**；降低 CPU 高挤占率是实现**手段**，不是硬性指标。**绝不**出现"CPU 明明还有空间给 OS/前台，却把本来能跑的进程降级"。

**推论（写入 SPEC §1/§5）**：
- 触发条件 ≠ "进程 CPU 高"。触发条件 = **系统 CPU 已饱和（无余量给 OS/前台）+ 该进程是挤占者 + 非关键（非前台/非系统关键）** 三者同时成立
- 系统还有余量（如 8 核空闲 2 核）→ 任何进程吃满 CPU 都合理 → **不干预**
- 直接信号（未来增强）：前台进程被饿着（runnable 等待）时优先响应；v1 保守实现用"系统总 CPU 接近饱和"作为拥挤度代理
- 启发式 v1 命名从"CPU 风暴检测"改为"**响应性保护（conservative）**"
- 前台保护、完整恢复、审阅面板信任闭环等既有定案不变

### 19.2 审阅面板三区布局定案（参照 Process Lasso）

**用户指示**：核心面板就三个区域——① 资源占用率时间线网格图 ② 当前全部进程情况（目标：比任务管理器更好用、响应更快）③ 操作日志审阅面板（现有）。

**图表方案调研（winui-search）结论**：

| 方案 | 优点 | 缺点 | 适配度 |
|---|---|---|---|
| **XAML Polyline + Points 绑定** | 零第三方依赖；几百点内性能开销极低（2s 采样、5 分钟视图≈150 点）；无原生库、无版本兼容风险；Fluent 主题/高对比度完全可控 | 无现成轴/网格/tooltip，需自绘静态网格线（Grid + Line + TextBlock，一次性成本） | ★★★★★ 推荐 |
| LiveCharts2（SkiaSharp） | 现成轴/网格/动画/缩放；官方有实时滚动示例 | 依赖 SkiaSharp 原生库（体积+兼容风险）；动画默认开（与"简洁无动画"定案相悖，需关）；包更新风险 | ★★★ 可选 |
| Win2D 自绘 | 性能天花板最高（上万点） | 代码量最大（坐标变换/轴/网格全手写）；我们点数规模用不上 | ★★ 过度设计 |

**定案**：时间线网格图用 **XAML Polyline + 静态 XAML 网格线**（零依赖 + 低开销 + 主题可控），数据 = samples 表最近 N 分钟（系统级 + 前台/关键进程级曲线）。进程表（区 2）用虚拟化 ListView（几百行必须虚拟化，与日志列表的非虚拟化 StackPanel 决策不同——行数数量级不同）。区 2 数据源需新增 gRPC 快照查询（最新每进程样本），区 1 用 QueryEvents 既有能力。

### 19.3 WatchEvents 真推送 + 抗卡顿要求

**用户指示**：认可升级为真实流推送；但**抗卡顿是硬要求**——即使 OS 卡（键盘/鼠标响应慢），用户也要能快速通过工具确认 ① ProBalance 是否生效 ② 进程占用率是否快速刷新，帮助定位问题。

**推论**：
- 推送通道不能依赖会被系统卡顿拖慢的路径：内存广播（评估时直接推订阅流）优于 DB 轮询（卡顿时 I/O 排队）
- app 侧数据面（进程快照/时间线）也要走"内存态 + 低开销渲染"，不能因系统忙而大幅延迟
- UI 渲染保持轻量（无动画、非虚拟化小列表、增量更新）——系统忙时 UI 线程要让给数据渲染
- 采样时间戳真实反映采集时刻（用户对比"卡顿时数据是否还在走"）
- 这是 M3 收尾项；验收时用 CPU 风暴实机压测"卡顿时面板数据仍流畅刷新"

## 2026-08-16 会话 ⑳ — 启发式 v1（响应性保护）落地 + 前台检测接入

**背景**：会话⑲定案启发式目标（响应性导向）后本会话实现。实现过程中发现启发式实机生效的前置依赖——前台检测——尚未接入（PolicyRunner.ForegroundPid 恒为 null → 启发式永远保守跳过），一并落地。

### 20.1 启发式 v1（core/PolicyEngine + HeuristicConfig）

- **触发 = 三条件齐备**（会话⑲定案）：① 系统 CPU 饱和（默认 ≥90%，`HeuristicConfig.SystemSaturationPercent`）② 进程挤占（默认 ≥50% 单核，`ProcessCpuPercent`）③ 非关键（非前台 + 非系统关键名单）
- **动作保守**：SetPriority → BelowNormal（0x4000），时长 30s（`DurationMs`），超时由 ExecutionPath 自动恢复；全部参数化（`HeuristicConfig`，SPEC §7 配置化定案）
- **规则始终优先**：显式规则命中的进程不走启发式（同一引擎两个配置面）
- **无前台信息 = 启发式整体保守跳过**（SPEC §6 定案：不主动降后台进程）
- 系统关键名单含引擎自身 `cpo.service`/`cpo.app`——**实机发现**：系统 100% 饱和时采样/评估进程自身 CPU 也高，启发式差点把自己降了（已修 + 测试 SkipsEngineItself）
- Trigger 值 `heuristic.saturation`，理由含系统 CPU + 进程 CPU + 时长（决策日志可解释）

### 20.2 前台检测接入（SPEC §6 定案落地：GUI 侧检测 → 管道上报）

- `app/Cpo.App/Native/ForegroundWatcher.cs`：SetWinEventHook(EVENT_SYSTEM_FOREGROUND) + GetForegroundWindow/GetWindowThreadProcessId（P/Invoke），UI 线程注册（WINEVENT_OUTOFCONTEXT 回调即 UI 线程）
- proto 新 RPC `ReportForeground(ForegroundReportRequest{pid,name})`；service 侧：`PolicyRunner.ForegroundPid`（volatile int，-1 哨兵规避 Nullable volatile 限制）+ 落盘 `ui.foreground` 事件（schema §4 首次真实产生）
- app 上报时机：hook 事件 + 启动时立即上报 + **每次新握手后补报**（service 重启自愈——首次握手结果曾被丢弃导致不补报，已修）
- 上报失败静默（无令牌时），Poll 补报兜底

### 20.3 验证（全链路实机）

- ✅ 121/121 单测全绿（+5：HeuristicTests 10 个含 SkipsEngineItself、PolicyRunner 启发式集成 3 个、gRPC ReportForeground 1 个、ReplayRunner 启发式 1 个）
- ✅ ui.foreground 实时上报：app 启动 → explorer → QQ 切换均落盘（`{"pid":32172,"name":"QQ"}`）
- ✅ **启发式实机触发**：2×隐藏 powershell 风暴 → 系统 100% → `policy.decision` trigger=`heuristic.saturation`（powershell 97% → BelowNormal/30s），决策理由完整可解释
- ✅ **30s 自动恢复**：`policy.action` kind=`restore` 落盘；进程已消失的干预静默移除（ReapExpired 既有设计）
- ✅ 引擎不自伤：第二轮风暴只降 powershell ×2 + chrome，无 Cpo.Service

### 20.4 坑（proto 定义顺序）

- **protoc（Grpc.Tools 2.67）要求 service 引用的 message 必须先定义**（本会话实测：message 定义在 service 之后报 "XXX is not defined"，移动到 service 之前即通过；与 proto3 规范的前向引用允许相悖，工具版本行为如此）——已在 AGENTS.md 坑表记录
- C# 侧：RPC 名 `ReportForeground` vs message 名 `ForegroundReportRequest/Response` 易写反（本会话把 Response 写成 `ReportForegroundResponse` 排查良久，最终是名字笔误，非元数据问题）

### 20.5 遗留
- 前台检测的"窗口标题"字段留 null（隐私红线：窗口标题不落盘）
- 启发式 v1 仅"系统饱和 + 挤占"一种触发；"前台进程被饿着"直接信号留作 v2 增强
- 启发式参数（阈值/时长/强度）尚未暴露到 UI/配置文件（SPEC §7 配置管线，M3 面板改造时一并做）

## 2026-08-16 会话 ⑳b — 启发式策略化升级：前台进程树保护 + 近期前台温和降级 + 条件解除提前恢复

**背景**：用户指出 v1 启发式"死板"（恢复只能等超时），并提出关键洞察——要分辨用户高频访问的前台程序，降级要谨慎；区分前台进程与它发起的子进程（保证前台程序响应即可）。用户授权我拿主意，设计为"三档保护"。

### 20b.1 三档保护（进程与用户活动的关联度）

| 档位 | 判定 | 策略 |
|---|---|---|
| 前台进程树 | 前台进程 + 全部后代（Toolhelp32 枚举父子关系，`ProcessController.GetDescendantPids`） | **绝不干预**（用户当前直接活动） |
| 近期前台程序 | 过去 1h 内曾为前台（`ui.foreground` 数据，service 内存维护 `_recentForegroundMs`） | **温和降级**：挤占阈值 50%→80% + 时长 30s→10s（`HeuristicConfig.RecentForeground*` 参数化） |
| 普通后台 | 其余 | v1 行为（50% / 30s） |

- 前台树枚举失败 → 空集合（保守：不额外保护，但前台进程本身仍受保护）
- 近期前台历史是**内存态**：service 重启后从零积累（app 补报当前前台即恢复）
- 理由文案区分"近期前台程序……谨慎降级"，决策日志可解释

### 20b.2 条件解除提前恢复（治"死板"）

- 每轮评估：生效中的**启发式干预**（RuleId==null）目标进程当前 CPU 已 < 挤占阈值（50%）→ **立即恢复原值**，不等 30s 超时
- **规则干预不提前恢复**（尊重用户显式规则语义，避免"规则永远降优"场景降→恢复→降抖动）——`ActiveIntervention` 新增 `RuleId` 字段区分来源
- 防抖复用 ExecutionPath 冷却（恢复后 30s 不重复降）✓

### 20b.3 实机事故与修复：service 被 SQLite disk I/O error 杀死

- **事故**：16 核风暴压测期间 service 崩溃（事件日志：未处理 `SqliteException: SQLite Error 10: disk I/O error`），遥测停在 16:54，进程裸崩
- **根因**：`TelemetryRecorder.RunAsync` 采样/落盘路径**无 try/catch**（EvaluateLoop/PurgeLoop/GatekeeperPipe 都有，唯独 recorder 漏了）——一次写失败直接杀进程
- **修复**：① recorder 循环体 try/catch（单次失败记录后继续，与评估循环同模式）② Program.Main 全局兜底 catch（任何未预期异常走正常收尾：恢复干预 + 停止服务，不裸奔）
- 疑因：系统饱和 + 杀软扫描锁 .db 文件（Windows 已知现象）；修复后低负载不再复现，高负载容错兜底
- 顺带发现：系统饱和时采样滞后（5s 窗口查不到新样本）→ 启发式输入只剩旧样本进程——已知特性（AGENTS 坑表），未修

### 20b.4 验证

- ✅ 127/127 单测全绿（+6：树保护引擎层/温和阈值与时长/非近期走标准参数/PolicyRunner 树集成/条件解除恢复/规则干预不提前恢复）
- ✅ 实机：service 重启自愈 + app 补报前台（ui.foreground 新事件）✓；启发式持续工作（chrome 挤占被降 + cooldown 防抖）
- ✅ 条件解除恢复实机证据：MiniMax Code 干预在评估轮恢复后立即解除（CPU 已低）
- 受控实验受"饱和时采样滞后"干扰（chrome 抢占决策、powershell 样本缺失），条件解除严格性以单元测试为准
- 清理后环境正常：service + app 运行中，用户 chrome 自身高负载由启发式持续处理（真实场景）

### 20b.5 遗留
- 系统饱和时采样滞后（5s 窗口）导致部分进程缺席输入——后续可考虑"窗口放宽到 2×采样间隔"或进程枚举直接进引擎输入
- 近期前台历史内存态：将来可持久化（重启后保留"高频程序"画像）
- `policy.action.kind` 实际序列化为 camelCase（"setPriority"），与 schema §6 文档（set_priority）不一致——文档待同步

## 2026-08-16 会话 ⑳c — 设计修正：前台子进程回标准档（"树内绝不降"是过度保护）

**背景**：用户指出上一轮"前台进程树绝不干预"过度保护——IDE / AI agent 发起的 rg.exe、编译器等工具子进程高频占用 CPU，**它们是可以降的**：降优先级（CPU 不够用影响前台时）的代价仅仅是慢一些，不影响前台进程响应度（前台进程本身仍 Normal，调度器优先给它）。

### 20c.1 定案：两档保护（修正三档）

| 档位 | 判定 | 策略 |
|---|---|---|
| 前台进程本身（精确 PID 匹配） | 用户直接交互 | 绝不降 |
| 近期前台程序（1h 窗口，仅前台本身 pid） | 用户高频使用 | 温和降（80% / 10s） |
| **其余一切（含前台子进程、普通后台）** | —— | 标准降（50% / 30s） |

- **关键洞察**：Windows 优先级只影响目标进程自身调度；子进程降为 BelowNormal 时前台进程仍 Normal → 前台响应度零影响。工具子进程（rg/编译/索引/AI worker）正是风暴常见元凶，应该被正常处理
- **取舍**：浏览器渲染进程等"交互敏感子进程"会被标准降级误伤（系统饱和时页面可能掉帧）——但触发前提是系统已 ≥90% 饱和（反正全局在卡），且 BelowNormal 仅降一级、30s/条件解除即恢复，误伤窗口小。交互敏感子进程的类型识别留作 v2 精细化
- 引擎侧删除：`EngineInput.ForegroundTreePids`、PolicyRunner 每 2s Toolhelp32 树枚举、`IProcessController.GetDescendantPids` 接口方法（interop 静态实现保留，供 M3 进程表 UI 树视图）
- `_recentForegroundMs` 只记前台本身 pid（子进程不算"高频程序"）——现状已如此，明确为语义

### 20c.2 采样滞后实证（遗留，未修）

- 16 核风暴（16×powershell 100%）实测：**系统饱和时采样器失效**——风暴进程样本 CPU=0 或缺失，启发式只能看见饱和前的常驻进程样本（chrome/Taskmgr/MiniMax 被正确降，风暴进程缺席）
- LookbackMs 5s→15s 无效（问题不在窗口，在采样本身：进程枚举/CPU 时间读取在 100% 调度下饿死）
- 影响：饱和风暴场景降不到"新生风暴进程"，只降常驻挤占者——功能打折，非灾难（风暴短暂）
- 修法候选（M3 后）：采样线程提优先级 / ETW 或 PDH 性能计数器替代轮询枚举——架构级改动，另行立项

### 20c.3 验证
- ✅ 127/127 全绿（子进程测试改写：引擎层 ForegroundChildProcess_StandardDowngrade + PolicyRunner 集成 DowngradesForegroundChildProcess）
- ✅ 实机：启发式链路正常（饱和时 Taskmgr/chrome/MiniMax 常驻挤占者被降、动作/恢复落盘）；前台本身不降（MiniMax 前台期间无自身降级记录）
- 环境：service（35020）+ app（35376）运行中，风暴已清
