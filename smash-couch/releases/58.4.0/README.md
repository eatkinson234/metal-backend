# Smash Couch 58.4.0 / 原生 Metal .58 r21

对应 2026-10-13 01:03 MDT 的本机安装。已核对原可执行文件、运行清单与源提交。新 r21 pacing 已启用，原 legacy marker 已移除。[启动器下载](../../downloads)、[使用说明](../../HOW-TO-USE.md)。

## 完整源码重建

从仓库根目录，在新目录操作：

```sh
git clone https://github.com/skybuleli/Ryubing.git /path/to/ryubing-r21
git -C /path/to/ryubing-r21 checkout --detach eb6b0e9adca0c2f010fa6ac093c806c4db199283
git -C /path/to/ryubing-r21 apply "$PWD/smash-couch/emulator/native-metal53.patch"
python3 smash-couch/emulator/verify-source.py /path/to/ryubing-r21
git -C /path/to/ryubing-r21 apply "$PWD/smash-couch/releases/58.2.0/emulator/native-metal58-from53.patch"
python3 smash-couch/releases/58.2.0/emulator/verify-source.py /path/to/ryubing-r21
git -C /path/to/ryubing-r21 apply "$PWD/smash-couch/releases/58.3.0/emulator/native-metal58-r16-from-r15.patch"
python3 smash-couch/releases/58.3.0/emulator/verify-source.py /path/to/ryubing-r21
git -C /path/to/ryubing-r21 apply --check "$PWD/smash-couch/releases/58.4.0/emulator/native-metal-r21-from-r16.patch"
git -C /path/to/ryubing-r21 apply "$PWD/smash-couch/releases/58.4.0/emulator/native-metal-r21-from-r16.patch"
python3 smash-couch/releases/58.4.0/emulator/verify-source.py /path/to/ryubing-r21
```

完整 5,198 文件身份见 [SOURCE-MANIFEST.json](emulator/SOURCE-MANIFEST.json)。覆盖层仅为 r16→r21 的 17 个变更/新增文件；不能代替完整源码。原生桥仍沿用 `smash-couch/emulator/native/`。

## 构建与分发边界

沿用 [58.2 构建环境/依赖和打包说明](../58.2.0/README.md)，版本参数改为 `-p:Version=1.3.3-metal.58-correctness-r21`。原机冻结 .NET SDK 9.0.305 / 165 包离线闭包、自包含 osx-arm64 发布、应用壳修复和签名过程不能等同于通用一条命令安装器。完整游戏运行包未上传。

启动器：

```sh
cd smash-couch/releases/58.4.0/launcher
bash ./run-cpu-checks.sh
bash ./build-launcher.sh
```

脚本固定 SDK 26.5、arm64 目标，拒绝覆盖构建。公开夹具脱敏及测试摘要调整已记录；生产源码与门禁保持原样，因此私有种子门禁仍会拒绝公开脱敏会话。原机 127 通过记录与公开测试限制分别报告。下载原应用是 ad hoc 签名且未公证，只有兼容批准的本地运行包与配置才能启动游戏。

## 原机回滚

本次 GitHub 发布不执行回滚。正常关闭游戏和大厅后，在原机使用已有工具：

```sh
"$HOME/Desktop/Smash Couch/work/overnight-correctness-r1/r21-undo.sh"
```

该工具恢复配对 58.3/r16 与 legacy marker；不修改游戏存档，不回滚着色器补充。机器专用工具、安装/缓存收据和备份不公开。缓存回滚单独按原项目 `MORNING-2026-10-13.md` 执行；已有游玩变化时不能无条件交换回旧缓存。

详见 [STATUS.md](../../STATUS.md)、[发布检查](PUBLICATION-CHECKS.json) 和 [构建身份](BUILD-IDENTITY.json)。
