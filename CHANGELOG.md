# 变更日志

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 格式，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [2026-10-11] — Smash Couch 58.2.0 原生 Metal 源码同步

- 同步已安装 .58 r15 模拟器与 58.2.0 启动器源码、身份清单、构建与回滚说明。
- 更新反馈绘制、后台编译与配方保存、输入队列、内存保留、退出与宿主访问崩溃修复的证据及边界。
- 记录用户最新流畅反馈；保留未覆盖场景和未安装 58.3/r16 工作的区别。
- 保留原项目及 .53 历史快照；无游戏文件、密钥、固件、存档、运行缓存或签名应用。

## [未发布]

### 新增
- 项目仓库模板初始化
- Phase 0（开发环境与工具链搭建）全部 15 个任务完成
- 4 个 Ally Skill 创建（toolchain/shader-debug/metal-api/gal-mapping）
- Agent 操作系统（AGENTS.md + PROGRESS.md + NEXT_TASK.md + SESSION_LOG.md）
- tools-verify.sh 环境验证脚本（15 项检查 + Path A 端到端）
- VS Code 工作区配置（扩展推荐/设置/启动/任务）
- CI/CD 流水线（GitHub Actions）

### 变更
- 无

### 弃用
- 无

### 移除
- 无

### 修复
- 无

### 安全
- 无