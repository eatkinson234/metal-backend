# Smash Couch 58.3.0 / 原生 Metal .58 r16

这是 2026-10-12 05:47 MDT 安装版本的源码快照，身份经过本次重新核对。当前启动器仍用默认轮询输入，运行包通过 `pacing-legacy` 保留 legacy pacing；事件输入与新 pacing 不能冒充当前已验收模式。

## 完整源码重建

从仓库根目录执行，使用新目录：

```sh
git clone https://github.com/skybuleli/Ryubing.git /path/to/ryubing58-r16
git -C /path/to/ryubing58-r16 checkout --detach eb6b0e9adca0c2f010fa6ac093c806c4db199283
git -C /path/to/ryubing58-r16 apply "$PWD/smash-couch/emulator/native-metal53.patch"
python3 smash-couch/emulator/verify-source.py /path/to/ryubing58-r16
git -C /path/to/ryubing58-r16 apply "$PWD/smash-couch/releases/58.2.0/emulator/native-metal58-from53.patch"
python3 smash-couch/releases/58.2.0/emulator/verify-source.py /path/to/ryubing58-r16
git -C /path/to/ryubing58-r16 apply --check "$PWD/smash-couch/releases/58.3.0/emulator/native-metal58-r16-from-r15.patch"
git -C /path/to/ryubing58-r16 apply "$PWD/smash-couch/releases/58.3.0/emulator/native-metal58-r16-from-r15.patch"
python3 smash-couch/releases/58.3.0/emulator/verify-source.py /path/to/ryubing58-r16
```

5,197 文件的完整哈希清单及源提交见 [emulator/SOURCE-MANIFEST.json](emulator/SOURCE-MANIFEST.json)。`overlay/` 为相对 r15 的 27 个变更/新增文件，不能代替完整源码。补丁重建已逐文件验证。

## 构建

模拟器沿用 [58.2.0 构建环境与打包限制](../58.2.0/README.md)：.NET SDK 9.0.305、Release/osx-arm64、自包含、单 MSBuild 工作进程、原生桥接依赖、应用修复与签名。将版本参数改为 `-p:Version=1.3.3-metal.58-correctness-r16`。原机 165 包离线依赖闭包、应用壳及私有配置不公开；源码相同不保证重建二进制完全相同，也不是便携签名安装器。

启动器：

```sh
cd smash-couch/releases/58.3.0/launcher
bash ./run-cpu-checks.sh
bash ./build-launcher.sh
```

脚本固定 macOS 26.5 SDK、arm64 目标，拒绝覆盖已有构建。公开夹具路径与对应测试摘要已脱敏；生产源码和门禁保持原样。已知私有种子门禁会拒绝脱敏测试会话，因此本次不宣称公开检查通过。127 项通过是原机安装记录；不绕过门禁以获得绿色结果。缺少批准运行包/配置时生成的启动器会拒绝启动。

## 本机回滚

本次发布不执行安装/回滚。正常关闭游戏与启动器，保留当前存档/缓存。在本机权威项目 `work/native-metal-full-prototype/native-stderr-direct-diagnostic-r1/couch58-launcher-r1` 使用已保存工具：

```sh
python3 -B deliver58.py rollback delivery/install-20261012T114703Z.json
```

该配对备份恢复 58.2.0 + r15；机器专用安装器、备份和门禁授权不公开。不要用旧存档覆盖新进度。legacy marker 在 r15 中不启用 r16 的新 pacing，后续实验仍须独立授权/验收。

详细记录见 [最新状态](../../STATUS.md) 与 [发布检查](PUBLICATION-CHECKS.json)。r19 是单独 [实验补丁](../../experiments/r19)，不是此安装版本。
