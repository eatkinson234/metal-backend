# Smash Couch 58.2.0 源码快照

对应已安装启动器 58.2.0 与模拟器 `1.3.3-metal.58-correctness-r15`。安装可执行文件的 SHA-256 已与本机部署记录核对；见 [BUILD-IDENTITY.json](BUILD-IDENTITY.json)。新版 58.3/r16 尚在开发，不属于此快照。

## 源码重建与校验

从仓库根目录执行，使用全新目标目录：

```sh
git clone https://github.com/skybuleli/Ryubing.git /path/to/ryubing58
git -C /path/to/ryubing58 checkout --detach eb6b0e9adca0c2f010fa6ac093c806c4db199283
git -C /path/to/ryubing58 apply --check "$PWD/smash-couch/emulator/native-metal53.patch"
git -C /path/to/ryubing58 apply "$PWD/smash-couch/emulator/native-metal53.patch"
python3 smash-couch/emulator/verify-source.py /path/to/ryubing58
git -C /path/to/ryubing58 apply --check "$PWD/smash-couch/releases/58.2.0/emulator/native-metal58-from53.patch"
git -C /path/to/ryubing58 apply "$PWD/smash-couch/releases/58.2.0/emulator/native-metal58-from53.patch"
python3 smash-couch/releases/58.2.0/emulator/verify-source.py /path/to/ryubing58
```

完整 5,185 文件清单见 [emulator/SOURCE-MANIFEST.json](emulator/SOURCE-MANIFEST.json)。`overlay/` 是相对于已发布 .53 的 31 个变更/新增文件；不能单独代替完整源码。此次隔离验证确认：公开 .53 完整源码身份一致，增量补丁可应用，结果逐文件等于 r15 源码。源码相同不保证二进制逐字节相同。

## 模拟器构建

原构建使用 .NET SDK 9.0.305、Release、osx-arm64、自包含发布、单个 MSBuild 工作进程。原机使用已冻结的 165 包离线依赖闭包；该机器专用依赖准备和签名包没有公开。准备对应 SDK 与项目依赖后，标准编译形式如下：

```sh
dotnet publish /path/to/ryubing58/src/Ryujinx/Ryujinx.csproj \
  -c Release -r osx-arm64 --self-contained true --disable-build-servers \
  -maxcpucount:1 -p:RuntimeIdentifiers=osx-arm64 -p:UseSharedCompilation=false \
  -p:NodeReuse=false -p:Version=1.3.3-metal.58-correctness-r15 \
  -o /path/to/new-publish
```

原生桥接库沿用 `smash-couch/emulator/native/` 的源码与构建脚本。应用打包还需要正确放置原生依赖、执行重建源码中的 `distribution/macos/bundle_fix_up.py` 并使用源代码随附 entitlements 签名/验证。原机过程复用已有 .53 应用壳并替换发布的单文件可执行程序；此源码快照不是可直接下载的签名安装器，也不宣称上述单条命令完成应用打包。另机需要独立构建与运行验收。

## 启动器构建与软件检查

```sh
cd smash-couch/releases/58.2.0/launcher
bash ./run-cpu-checks.sh
bash ./build-launcher.sh
```

Apple Silicon Mac 需要 Command Line Tools；原构建脚本明确固定 macOS 26.5 SDK 路径。脚本生成全新本地输出，拒绝覆盖已有构建。不应将生成物直接替换当前可用安装。源码包含运行时身份门禁；缺少本机批准的运行包/配置时，会拒绝启动。测试配置路径脱敏，对应测试身份摘要同步更新；原运行门禁常量和生产源码保持原样。测试应在规范、无符号链接路径下执行：macOS 的 /private/tmp 路径别名会触发严格会话目录校验。本次公开副本的全套检查未通过：脱敏配置已同步测试摘要，但门禁仍固定原私有种子，随后按设计拒绝测试会话。未修改生产门禁以让检查通过。原机安装记录为 90 项检查通过；它不是公开脱敏夹具可移植性的证明。软件检查不是物理控制器或 GPU 验收。

## 回滚

本次 GitHub 同步不执行安装或回滚。本机 58.2.0 的配对备份为 58.1.0 + r14。先正常退出游戏和启动器，保留当前存档与缓存；在权威本地项目 `work/native-metal-full-prototype/native-stderr-direct-diagnostic-r1/couch58-launcher-r1/` 中运行：

```sh
python3 -B deliver58.py rollback delivery/install-20261011T201109Z.json
```

该本机工具使用已核对的备份与安装收据；仓库不公开机器专用安装器、备份或授权门禁。不要用旧存档覆盖新进度，不要为回滚清空缓存。GitHub 代码回退可通过新分支检出此前提交，避免改写主分支历史。

更多已验证结果和剩余限制见 [STATUS.md](../../STATUS.md)。
