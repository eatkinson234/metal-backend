# AGENTS.md — Switch Metal 后端

> **此文件的作用**：所有 AI 编程 Agent（Cursor、Claude Code、Copilot、Windsurf、Devin）在启动会话时都会自动读取仓库根目录的 `AGENTS.md`。这是本项目对 Agent 行为的唯一权威约束文件。

---

## 1. 项目身份

**是什么**：为 Nintendo Switch 模拟器（基于 Ryubing）实现原生 Apple Metal 图形后端。
**目标**：在 macOS 上通过 Metal 直接运行 Switch 游戏，绕过 Vulkan→MoltenVK→Metal 的中间层损耗。
**不是**：不是替换 Ryubing、不支持非 Apple 平台、不是新模拟器。

**成功定义**：Top 20 Switch 游戏，≥15 款可玩且 ≥30fps，2D 游戏 ≥60fps，运行于 Apple Silicon Mac。

### ⚠️ 语言约束（顶级强制）

本项目维护者为中文母语者。**以下内容必须使用简体中文**：

- 所有代码注释（`//` 和 `/* */`）
- 所有 Git 提交信息
- 所有文档（AGENTS.md 除外，本文件为双语）
- 所有会话日志（SESSION_LOG.md）
- PROGRESS.md 中的任务描述和证据

**可以使用英文的例外**：代码标识符（变量名、函数名、类名、命名空间）、技术术语（如 DXIL、SPIR-V、metallib）、命令行参数。

> 违反此约束的提交应被拒绝或立即修正。

---

## 2. 运行系统 —— 四个文件驱动一切

本项目设计为**机器可读**。每个 Agent 会话都针对以下文件操作：

| 文件 | 角色 | 谁写 | 何时 |
|------|------|------|------|
| `AGENTS.md` | **入口文件**（本文件） | 人类 | 极少修改 |
| `PROGRESS.md` | **状态机** — 220 个任务，每个带状态 ⬜/🔄/✅/🚫/⏸️ | Agent | 每个任务完成后 |
| `NEXT_TASK.md` | **当前指令** — 从 PROGRESS.md 自动生成 | 脚本 | 每次提交后 |
| `SESSION_LOG.md` | **审计记录** — 每次会话干了什么 | Agent | 每次会话结束后 |
| `blueprint/` | **技术规格** — 完整架构、ADR、着色器路径 | 人类 | 方案变更时 |

**规则**：Agent **永远不需要猜测**该干什么。它读 `NEXT_TASK.md`（由 `PROGRESS.md` 生成，由 `blueprint/` 规划）。

---

## 3. Agent 会话协议（强制）

每个 Agent 会话必须严格遵守以下生命周期。**无例外**。

```
会话开始
  │
  ├─ 1. 读取  NEXT_TASK.md          ← 找到当前任务（如 P0.1）
  ├─ 2. 读取  blueprint/           ← 阅读本任务对应的技术规格章节
  ├─ 3. 认领  在 PROGRESS.md 中将任务标记为 🔄
  ├─ 4. 执行                        ← 干活
  ├─ 5. 验证  检查是否满足出口标准
  ├─ 6. 固化  在 PROGRESS.md 中标记 ✅，填入完成时间和证据路径
  ├─ 7. 提交  git add + git commit（约定式提交格式）
  ├─ 8. 生成  运行：python3 tools/gen_next_task.py
  ├─ 9. 记录  在 SESSION_LOG.md 末尾追加会话记录
  │
会话结束
```

### 任务状态（PROGRESS.md 中使用）

- `⬜` 待开始 — 尚未启动
- `🔄` 进行中 — **同时只能有一个**，禁止并行
- `✅` 已完成 — 必须附带时间戳和证据
- `🚫` 已阻塞 — 必须在阻塞表中添加原因
- `⏸️` 已跳过 — 有意跳过，须说明理由

---

## 4. 架构速查（精简版）

### 四个架构决策（ADR）—— 未经人类批准，禁止推翻

| ADR | 决策 | 理由 |
|-----|------|------|
| ADR-001 | Slang→DXIL→MSC 为主要着色器路径 | MSC 是 Apple 官方工具；Slang 有成熟 DXIL 后端；不存在 Maxwell→DXBC 编译器供 airconv 使用 |
| ADR-002 | Ryubing 作为模拟器基础 | 有 GAL 抽象层 + IRenderer/IPipeline 接口；Astris 无源码 |
| ADR-003 | C++/C# 混合架构 | metal-cpp（C++）实现零开销 Metal 调用；C# 实现 Ryubing GAL 接口，通过 P/Invoke 桥接 |
| ADR-004 | 5 条着色器路径冗余 | 运行时自动回退：路径 A → 路径 C → 路径 B → 报错 |

### 五条着色器路径

```
Maxwell SASS → Ryujinx 解码器 → 结构化 IR
    ├── CommandMapper → Slang 原生语法 (HLSL 风格)
    │     ├── 路径 A（主⭐）：→ Slang -target dxil → DXIL → MSC → metallib
    │     ├── 路径 B（备选）：→ Slang -target metal → MSL → xcrun metal → metallib
    │     └── 路径 D（优化）：→ Slang -target spirv → SPIR-V → spirv-opt → Slang -target dxil → MSC
    ├── CodeGen/Glsl → GLSL
    │     └── 路径 C（SPIR-V桥）：→ glslangValidator → SPIR-V → spirv-opt → SPIRV-Cross → MSL
    └── 路径 E（交叉验证）：→ shader-compiler-rs → GLSL' → 任意路径
```

> **P1 实验结论**: Path A/B/D 的输入改为 Slang 原生语法（非 GLSL），因为 GLSL 的 std140/push_constant 与 DXIL SM 6.0 不兼容。详见 `docs/shader-debug.md`。

### 三层 Maxwell 参考体系

- 第一层（寄存器级）：deko3d `engine_3d.def` + envytools `rnndb`
- 第二层（指令级）：NVK Maxwell 后端 + MaxAs + nvdisasm
- 第三层（着色器级）：Ryujinx 解码器 + shader-compiler-rs

### 运行时：libmetal_bridge（C++）

```
libmetal_bridge/
├── MetalDevice     — MTLDevice 创建、GPU 选择
├── MetalQueue      — MTLCommandQueue 管理
├── MetalBuffer     — MTLBuffer（StorageMode: Managed/Private/Shared）
├── MetalTexture    — MTLTexture + Maxwell→MTLPixelFormat 映射表
├── ShaderCompiler  — Slang API + libmetalirconverter + 缓存
├── CommandMapper   — Maxwell 状态 → Metal API（参考 deko3d）
└── Presenter       — CAMetalLayer、交换链
```

### Ryubing 集成方式

在 C# 中实现 `Ryujinx.Graphics.GAL.IRenderer` 和 `IPipeline`。C# 层通过 P/Invoke 调用 libmetal_bridge，经由 `MetalNative.cs`。

### 十个阶段

| 阶段 | 名称 | 周数 | 出口标准 |
|------|------|------|----------|
| P0 | 开发环境与工具链搭建 | 1-2 | tools-verify.sh 全部 OK |
| P1 | 着色器管道概念验证 | 2-3 | ≥3 条路径验证通过，≥3 个真实游戏着色器可编译 |
| P2 | 8 级渐进式渲染 Demo | 5-6 | D8 复杂 Demo ≥60fps |
| P3 | Ryubing Fork 与 GAL 集成 | 2-3 | Ryujinx.Graphics.Metal 编译通过 |
| P4 | 核心 Metal 后端实现 | 5-6 | 2D 游戏可启动渲染 |
| P5 | 命令映射与状态跟踪 | 3-4 | 3D homebrew 可渲染 |
| P6 | 测试与验证体系 | 3-4 | 50+ 测试用例通过，CI 绿色 |
| P7 | 性能优化与 MetalFX | 3-4 | D8≥60fps，3D≥30fps |
| P8 | 兼容性修复 | 3-4 | Top 20 游戏 ≥15 可玩 |
| P9 | 发布准备 | 1-2 | Public Beta 发布 |

---

## 5. 目录地图

```
switch-metal-backend/
├── AGENTS.md              ← 本文件 — Agent 入口
├── PROGRESS.md            ← 220 任务状态机
├── NEXT_TASK.md           ← 自动生成的当前任务
├── SESSION_LOG.md         ← 会话审计记录
├── README.md              ← 面向人类阅读的项目概述
├── CONTRIBUTING.md        ← 人类贡献指南
├── Makefile               ← 顶层构建编排
├── blueprint/            ← 完整技术规格
│   ├── architecture.md    ← ADR、着色器路径、libmetal_bridge 设计
│   └── agent-ops.md       ← Agent 操作系统详细设计
├── .githooks/pre-commit   ← 提交前自动验证进度 + 重新生成 NEXT_TASK
├── .github/               ← Issue/PR 模板
├── libmetal_bridge/       ← C++ Metal 运行时（metal-cpp）
├── ryubing/               ← 复刻的 Ryubing（含 Metal GAL 后端）
├── demos/                 ← D1–D8 渐进式渲染 Demo
├── tests/                 ← 50+ 测试用例（4 层金字塔）
├── tools/                 ← gen_next_task.py、verify_progress.py 等
├── smash-couch/           ← 用户要求同步的原生 Metal .53、启动器及 UI 源码快照
├── docs/                  ← 人类文档
└── ci/                    ← CI/CD 流水线
```

---

## 6. 环境约束

- **开发设备**: M1 Mac（8GB RAM / 256GB SSD），**仅有 Xcode Command Line Tools，无 Xcode.app**
- **着色器路径可用**: 路径 A（Slang→DXIL→MSC）✅ 完全可用；路径 B/C（需 xcrun metal）❌ 暂不可用
- **MSC 兼容性**: Apple 文档声称需 Xcode 15+，但实际可能仅需 CLT SDK。P0.1a 任务负责验证
- **C++ 编译**: metal-cpp 头文件可通过 CLT 自带的 macOS SDK 编译，不受影响
- **调试工具**: 无 Instruments / GPU Frame Capture，可用 RenderDoc Metal 插件或 Xcode GPU Capture（需另行安装）

## 7. 编程规范（严禁违反）

### 各语言规范

| 语言 | 标准 | 使用范围 | 关键规则 |
|------|------|----------|----------|
| C++ | C++17 | libmetal_bridge/ | 使用 metal-cpp，不用 Metal.framework 的 ObjC 头文件 |
| C# | C# 12 / .NET 8 | ryubing/ | 遵循 Ryubing 代码风格；使用 P/Invoke 桥接 C++ |
| MSL | Metal 3.0 | ShaderCompiler 输出 | 目标 Apple GPU Family 7+ |
| GLSL | 4.60 | ShaderCompiler 输入 | 仅限 Maxwell 兼容子集 |
| Python | 3.11+ | tools/、ci/ | 带类型标注；不依赖标准库之外的第三方包 |
| Shell | bash 3.2+ | tools/*.sh、ci/ | 严格模式：`set -euo pipefail` |

### 全局规则

1. **禁止臆测编码**：只实现当前任务要求的内容。禁止"顺便做"的额外改动。
2. **禁止凭空发明 API**：如果 Metal API 或 Ryubing 接口不在 `blueprint/` 或实际 Ryubing 源码中，禁止使用。
3. **文件路径神圣不可侵犯**：禁止在目录地图之外创建文件，除非同时更新本文件的目录地图。
4. **一个任务一次提交**：禁止将多个任务合并为一次提交。
5. **证据必须可查**：每个 ✅ 必须附带文件路径或 Shell 输出，证明任务确实完成了。
6. **所有注释、提交信息、会话日志必须使用中文**：本项目的维护者是中文母语者，Agent 在撰写代码注释、提交信息、会话日志时，必须使用简体中文。代码中的变量名、函数名、类名等技术标识符可以使用英文，但解释性的注释一律使用中文。

---

## 7. 防幻觉规则（每次会话前必须重读）

以下规则的存在是因为 AI Agent 会产生幻觉。认真读。严格遵守。

### 绝对禁令

1. **禁止凭空发明 Metal API**。如果你无法引用确切的 Apple 官方文档或 metal-cpp 头文件，就不要使用。本项目使用的所有合法 Metal API 必须可追溯到：
   - Apple Metal Shading Language Specification
   - SDK 中的 metal-cpp 头文件
   - `blueprint/architecture.md` 中批准使用的 API 列表

2. **禁止凭空发明 Ryubing 接口**。IRenderer 和 IPipeline 定义在真实的 Ryubing 源码 `src/Ryujinx.Graphics.GAL/` 中。必须读取源码确认，禁止臆测方法签名。

3. **禁止跳过验证**。每个任务都有出口标准。如果出口标准说"≥3 条路径验证通过"，你必须产出实际的编译输出，不能说"看起来没问题"。

4. **禁止无证据修改 PROGRESS.md**。每个 ✅ 必须包含：时间戳 + 证据文件路径（日志文件、截图描述、测试输出路径）。

5. **禁止同时执行两个任务**。PROGRESS.md 中只能有一个 🔄。

6. **禁止修改架构决策（ADR）**。ADR-001 到 ADR-004 是锁定的。如果你认为某个 ADR 需要修改，将当前任务标记为 🚫，并在阻塞表中说明理由。

7. **禁止使用未经验证的着色器编译路径**。ADR-004 中的 5 条路径是批准的集合。不要引入"路径 F"，除非你能够证明路径 A-E 在特定情况下全部失败且人类批准了新增路径。

8. **禁止未读规格就写代码**。执行任何任务前，必须先读：
   - `NEXT_TASK.md`（做什么）
   - `blueprint/architecture.md`（怎么做）
   - 相关 Ryubing 源码（如果涉及修改 Ryubing）

### 事实锚定

代码或注释中的每个事实性断言，必须锚定到以下来源之一：
- `blueprint/architecture.md` 中的某一行
- Apple Metal 官方文档中的某一页（注明文档名称）
- 真实 Ryubing 源码中的某一行（注明文件 + 行号）
- `workfiles/` 中已验证的实验输出

如果无法锚定 → 不要写。改为写 `// TODO: 待验证`。

---

## 8. 提交约定

```
<type>(<scope>): P{X}.{Y} <任务描述> [done]

type:  feat | fix | docs | test | refactor | perf | build | ci | chore
scope: tools | demo | gal | metal | shader | cmd | test | docs

示例：
feat(shader): P4.2.1 实现 Slang→DXIL 编译管线 [done]
```

每次提交必须：
1. 引用精确的任务编号（P{X}.{Y}）
2. 任务完成以 `[done]` 结尾，进行中以 `[wip]` 结尾
3. 使用正确的 scope：

| 阶段 | Scope |
|------|-------|
| P0 | tools |
| P1 | tools |
| P2 | demo |
| P3 | gal |
| P4 | metal |
| P5 | cmd |
| P6 | test |
| P7 | perf |
| P8 | fix |
| P9 | docs |

**提交信息必须使用中文**。技术标识符（如 P4.2.1、Slang、DXIL）可使用英文，但描述部分必须为中文。

---

## 9. 工具脚本参考

| 脚本 | 用途 | 何时运行 |
|------|------|----------|
| `tools/gen_next_task.py` | 解析 PROGRESS.md → 生成 NEXT_TASK.md | 每次任务提交后 |
| `tools/verify_progress.py` | 验证 PROGRESS.md 格式与一致性 | 每次提交前（由 pre-commit 钩子自动调用） |
| `.githooks/pre-commit` | 提交前运行验证 + 生成 | `git commit` 时自动触发 |
| `Makefile` | 构建编排 | `make build`、`make test`、`make verify` |

### Pre-commit 钩子流程

```
git commit
  → .githooks/pre-commit 触发
    → python3 tools/verify_progress.py   （验证失败则阻止提交）
    → python3 tools/gen_next_task.py     （更新 NEXT_TASK.md）
    → git add NEXT_TASK.md
  → 提交继续
```

---

## 10. 应急处理

### 任务被阻塞（🚫）

1. 在 PROGRESS.md 中将任务标记为 🚫
2. 在阻塞表中添加行：阻塞ID、描述、受影响的任务ID、提议的解决方案
3. 如果其他任务可以解除阻塞 → 转去执行那个任务
4. 如果需要人类介入 → 停止，在 SESSION_LOG.md 中报告阻塞，不要继续

### 构建失败

1. 禁止继续下一个任务
2. 先修复构建，其他一切靠后
3. 在 SESSION_LOG.md 中记录修复过程

### 与蓝图冲突

1. 蓝图优先于你的记忆
2. 如果蓝图看起来有误，将任务标记为 🚫 并附上解释
3. 禁止"修复"蓝图 — 那是人类的决策

---

## 11. 快速参考：启动新 Agent 会话

```
# 第 1 步：读取当前任务
cat NEXT_TASK.md

# 第 2 步：读取相关技术规格
cat blueprint/architecture.md | grep -A 50 "你的任务编号"

# 第 3 步：查看已完成情况
cat PROGRESS.md | head -20

# 第 4 步：标记任务为进行中
# （编辑 PROGRESS.md：将你的任务的 ⬜ 改为 🔄）

# 第 5 步：执行
# （干活）

# 第 6 步：验证并提交
python3 tools/verify_progress.py
git add -A
git commit -m "feat(scope): P{X}.{Y} 任务描述 [done]"
python3 tools/gen_next_task.py

# 第 7 步：记录
# 追加到 SESSION_LOG.md
```

---

## 12. 参考文献索引

| 文档 | 位置 | 何时阅读 |
|------|------|----------|
| 完整蓝图（V8） | `deliverables/switch-macos-metal-backend-blueprint.html` | 深入了解任何架构决策 |
| Agent 操作手册 | `blueprint/agent-ops.md` | Agent 系统设计细节 |
| 架构规格 | `blueprint/architecture.md` | 技术规格（ADR、着色器路径、模块设计） |
| Ryubing IPipeline | `src/Ryujinx.Graphics.GAL/IPipeline.cs`（在 Ryubing fork 中） | 实现 GAL 接口时 |
| Apple Metal 文档 | https://developer.apple.com/metal/ | Metal API 参考 |
| Metal Shader Converter | https://developer.apple.com/metal/shader-converter/ | MSC 用法与 CLI 参数 |
| Slang 文档 | https://shader-slang.org/ | Slang 编译器参数与目标 |
| deko3d | https://github.com/devkitPro/deko3d | Maxwell 寄存器参考（engine_3d.def） |
| envytools | https://github.com/envytools/envytools | Maxwell 硬件寄存器数据库（rnndb） |

---

## 13. 知识库索引（按需读取）

> AGENTS.md 只保留规则和协议。具体领域的参考知识拆分到 `docs/` 下，Agent 根据当前任务按需读取。

| 任务阶段 | 需要时读取 | 内容 |
|----------|-----------|------|
| P0~P1 工具链使用 | `docs/toolchain.md` | 工具路径、版本、命令速查、已知陷阱、环境约束 |
| P1 着色器验证 | `docs/shader-debug.md` | SPIR-V 验证流程、常见编译错误、健康检查 |
| P3 GAL 集成 | `docs/gal-mapping.md` | IPipeline→Metal API 映射表、63 方法分组 |
| P4 Metal 实现 | `docs/metal-api.md` | metal-cpp 核心模式：Device/Buffer/Texture/RenderPipeline |

---

*本文件最后更新：2026-10-09（新增 Smash Couch 快照入口）。*

## Smash Couch 源码快照

2026-10-09 用户要求同步的本地 Smash Couch 工作位于 `smash-couch/`；入口是其 `README.md`、`STATUS.md` 和 `BUILD.md`。这是保留原始源码语言和精确哈希的独立快照，不将旧蓝图任务标记为完成，也不改变原有 ADR。该快照的源码身份、诊断证据、运行验收和安装状态分别记录；后续运行仍需当前用户授权和原项目的准入检查。
