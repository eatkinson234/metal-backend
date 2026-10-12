# Smash Couch 启动器 58.2.0

这是已安装版本对应的源码，而不是当前工作目录中的 58.3 候选。来源提交、包含文件与测试夹具路径脱敏记录见 [SNAPSHOT.json](SNAPSHOT.json)。

保留 1–8 逻辑槽位、明确加入/重连、配置架构校验、运行包身份门禁与配对回滚。默认 Smash 标准映射：Xbox A 为 Switch B，Xbox B 为 Switch A，Xbox X 为 Switch Y，Xbox Y 为 Switch X；游戏内可修改控制。

```sh
bash ./run-cpu-checks.sh
bash ./build-launcher.sh
```

构建使用脚本中固定的 macOS 26.5 SDK 与 arm64 目标。测试在临时构建目录内生成模拟夹具，不启动游戏。构建结果需要批准的本地运行包和配置才能通过门禁；不是独立便携发行包。公开脱敏夹具与固定私有种子门禁不一致，本次重新运行全套检查被门禁拒绝；生产门禁保持原样，不能把本次检查称为通过。完整重建说明与验证边界见 [上级说明](../README.md)。
