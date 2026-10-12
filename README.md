# 最新 Smash Couch 进度 — 2026-10-11

**当前已安装：Smash Couch 58.2.0 / 原生 Metal .58 r15。** 用户最新反馈：“It is super smooth now which is awesome”。

最新源码、完整身份清单、构建和回滚说明位于 [smash-couch/releases/58.2.0](smash-couch/releases/58.2.0)。[最新状态](smash-couch/STATUS.md) 区分已安装修复、已有测试证据、尚未覆盖的情况，以及未安装的 58.3/r16 开发。历史 .53 和原项目源码保持保留。

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
