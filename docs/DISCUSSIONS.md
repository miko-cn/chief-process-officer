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
