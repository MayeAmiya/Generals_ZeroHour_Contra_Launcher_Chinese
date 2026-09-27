# Contra Launcher 中文版 / Contra Launcher Chinese Edition

[C91 Launcher](https://github.com/ContraMod/Launcher) 的中文分支，为 Contra X 汉化整合包提供启动器：深度适配 Generals Online（将军在线）、镜头控制、画质选项，并通过自己的 Cloudflare R2 渠道完成自更新与文件修复。

A Chinese fork of the official [Contra launcher](https://github.com/ContraMod/Launcher), tailored for the Contra X Chinese distribution: deep Generals Online integration, camera controls, quality options, and self-update / install repair through our own Cloudflare R2 channel.

## 功能 / Features

### 启动版本选择 / Version dropdown
主界面 LAUNCH 按钮旁边的下拉框选择本次启动的目标（选择立即保存，默认「默认」）：

The dropdown next to the LAUNCH button picks which build starts (persisted immediately, defaults to 默认):

| 选项 / Option | 启动目标 / Target |
| --- | --- |
| `默认` (Default) | 原版 Contra（generals.ctr 交换启动）/ Vanilla Contra via the generals.ctr swap |
| `GeneralsOnline` | 官方将军在线客户端（按 settings.json 的 anticheat 配置走 EAC 包装器或 `GeneralsOnlineZH_60.exe`）/ Official GO client (EAC wrapper or `GeneralsOnlineZH_60.exe`) |
| `GeneralsOnlineUnlimited` | 修改版 `GeneralsOnlineZH_Unlimited.exe`（无限制镜头，自动去除反作弊插件——EAC 拒绝修改过的可执行文件）/ Our modified unlimited client (anticheat plugin dropped, since EAC rejects modified executables) |

### 镜头控制 / Camera control
- 俯仰角与拉远高度滑块：原版模式写入 GenTool 的 `d3d8.cfg`（`[gentool76]` 节与平面键同步管理、去重、窗口预设固定 TOP），GO 模式写入客户端 `settings.json` 的 `camera` 对象。
- 游戏退出后自动从配置文件回读——游戏内 PageUp/PageDown 或 GenTool 的修改不会丢。
- / Pitch and zoom-out sliders write GenTool's `d3d8.cfg` (vanilla) or the client's `settings.json` (GO); values are re-read from the files whenever the game exits.

### 画质 / Quality
- MSAA+Filter 组合档位（关闭/2X/4X/8X），经 `Options.ini` 的 `AntiAliasing` 键生效；原版引擎忽略该键。
- 分辨率选择与 GO 启动参数（`-win -xres -yres`、`-disableCommunityDataPatch`）自动传递。
- / A combined MSAA + filter tier persisted through the `AntiAliasing` key the GO client reads; resolution and windowed arguments follow the launch.

### 自更新与安装修复 / Self-update & install repair
- 更新渠道：Cloudflare R2 桶 `contrax-release`（`https://dl.mayeamiya.dev/`）。启动器读取桶根目录的 `Versions_X.txt`（`Launcher: x.y.z$...` 格式），有新版本时下载 `Contra_Launcher.zip` 解压并重启；MOTD（公告）同样来自该文件。桶内容未就绪时全部静默跳过。
- `Contra_FileList.txt`（放在启动器旁）驱动的安装校验：缺失文件按分类补全——`ZH_GENERALS`/`ZH` 从注册表定位的本机安装拷贝（零售 EA App、Steam `ZeroHour` 键、十周年版；32/64 位视图都查），`ENGINE`/`MOD` 从 R2 下载。清单缺失时功能休眠。
- / Updates and MOTD come from our R2 bucket; a manifest next to the launcher (`Contra_FileList.txt`) repairs missing files by copying from registry-located local installs or downloading from R2.

### 其他 / Misc
- 简体中文界面自动跟随系统区域；GO 客户端 `settings.json` 首次启动自动生成；GenTool 相机写入兼容 7.6+ 布局。

## 使用 / Usage

把启动器放进游戏目录（绝命时刻主目录，Contra X 整合包根目录），直接运行 `Contra_Launcher.exe`。启动器会自动定位文档目录、生成缺失的 `Options.ini` / `settings.json`、校验安装文件，然后按下拉框选中的版本启动。

Place the launcher in the Zero Hour directory (the Contra X distribution root) and run it. It locates the Documents folder, generates missing `Options.ini` / `settings.json`, verifies the installation, then starts whatever the version dropdown selected.

## 构建 / Compiling

Requires Windows and one of the following SDKs:

**.NET 10** (netcore, single-file publish)

```
dotnet publish /p:Configuration=Release netcore/netcore.csproj
```

Build Location: `netcore\bin\Release\net10.0-windows\win-x86\publish\Contra_Launcher.exe`（需目标机安装 .NET 10 Desktop Runtime）

**.NET Framework 4.8** (net48)

```
msbuild /p:Configuration=Release /p:Platform=AnyCPU net48/net48.csproj
```

Build Location: `net48\bin\Release\Contra_Launcher.exe`

用 Visual Studio 打开解决方案构建亦可 / Building from Visual Studio works as well.

## R2 桶文件布局 / Bucket layout (contrax-release)

```
Versions_X.txt          # Launcher: 2.0.0.9$...（MOTD 可跟在后面）
Contra_Launcher.zip     # 自更新包
<Contra_FileList.txt 中列出的相对路径>  # ENGINE / MOD 修复文件
```

## 致谢 / Credits

- [Contra Project Team](https://www.moddb.com/mods/contra) —— 原版启动器与 MOD（MIT，见 LICENSE.txt）
- [GeneralsGameCode / TheSuperHackers](https://github.com/TheSuperHackers/GeneralsGameCode) —— 开源引擎与安装注册表契约
- [Generals Online](https://generals.online/) —— 将军在线
