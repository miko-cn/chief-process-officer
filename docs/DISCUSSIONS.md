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
