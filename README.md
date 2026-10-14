# 最新 Smash Couch 进度 — 2026-10-13

**当前正式安装：Smash Couch 58.4.0 + 原生 Metal .58 r21。** 新帧节奏已启用，版本/可执行文件/包清单均重新核对。

[下载 Smash Couch 启动器应用](smash-couch/downloads) · [How to use / 使用说明](smash-couch/HOW-TO-USE.md) · [最新进度](smash-couch/STATUS.md) · [源码/构建/回滚](smash-couch/releases/58.4.0)

下载仅含大厅应用，依赖本地批准运行包；**不含 Smash Bros 游戏、模拟器应用、密钥、固件、存档或缓存**。短队列与内存候选仍未安装；已知限制明确列出，历史快照与原项目保持保留。

---

# Switch Metal 后端

> 为 Nintendo Switch 模拟器（基于 Ryubing）实现的原生 Apple Metal 图形后端。

## 这是什么

将 Maxwell GPU 指令直接翻译为 Metal API 调用。绕过 Vulkan→MoltenVK→Metal 的中间层。

## 快速开始

**面向人类：**
```bash
bash tools/tools-verify.sh  # 验证工具链
cat NEXT_TASK.md             # 查看下一步做什么
```

**面向 AI Agent：** 从 [`AGENTS.md`](AGENTS.md) 开始 —— 这是机器可读的入口文件，告诉任何 AI 编程 Agent 如何在这个仓库中工作。

## 项目结构
```
├── AGENTS.md          ← 🤖 AI Agent 入口（先读这个）
├── PROGRESS.md        ← 🔴 项目状态机（132 个任务）
├── NEXT_TASK.md       ← 🔴 自动生成的当前任务
├── SESSION_LOG.md     ← 📝 会话审计记录
├── blueprint/        ← 📋 技术规格
├── src/libmetal_bridge/   ← 🔧 C++ Metal 运行时
├── src/ryubing/           ← 🕹️ 复刻的模拟器
├── src/demos/             ← 🎨 D1–D8 渐进式渲染 Demo
├── src/tests/             ← 🧪 测试金字塔
├── tools/             ← 🛠️ 工具脚本
└── .github/           ← 🔄 CI/CD 模板
```

## Agent 工作流
```
NEXT_TASK.md → 在 PROGRESS.md 中认领 → 执行 → 验证 → 提交 → gen_next_task.py → 重复
```
详见 [`AGENTS.md`](AGENTS.md) 获取完整 Agent 协议。详见 `blueprint/agent-ops.md` 获取系统设计细节。Phase 2 的 Demo 证据规范见 `docs/p2-demo-evidence.md`。

## 技术栈
| 组件 | 语言 | 框架 |
|------|------|------|
| Metal 运行时 | C++17 | metal-cpp |
| 模拟器 | C# 12 (.NET 8) | Ryubing |
| 着色器管线 | GLSL→Slang→DXIL→MSC | CLI + C API |
| 构建系统 | CMake + MSBuild + Make | — |
| 测试 | pytest + GoogleTest + xUnit | — |

## 许可证
MIT
