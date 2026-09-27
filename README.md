# Contra Launcher 中文版 / Contra Launcher Chinese Edition

[C91 Launcher](https://github.com/ContraMod/Launcher) 的中文分支，为 Contra X 汉化整合包提供启动器：深度适配 Generals Online（将军在线）、镜头控制、画质选项，并通过自己的 Cloudflare R2 渠道完成自更新与文件修复。

A Chinese fork of the official [Contra launcher](https://github.com/ContraMod/Launcher), tailored for the Contra X Chinese distribution: deep Generals Online integration, camera controls, quality options, and self-update / install repair through our own Cloudflare R2 channel.

## 功能 / Features

### 启动版本选择 / Version dropdown
主界面 LAUNCH 按钮旁边的下拉框选择本次启动的目标（选择立即保存，默认「将军原版」；中文界面第一项显示「将军原版」，其他语言显示 `GeneralsOriginal`）：

The dropdown next to the LAUNCH button picks which build starts (persisted immediately, defaults to GeneralsOriginal):

| 选项 / Option | 启动目标 / Target |
| --- | --- |
| `将军原版` (GeneralsOriginal) | 原版 Contra（generals.ctr 交换启动）/ Vanilla Contra via the generals.ctr swap |
| `将军在线` (GeneralsOnline) | 官方将军在线客户端（按 settings.json 的 anticheat 配置走 EAC 包装器或 `GeneralsOnlineZH_60.exe`）/ Official GO client (EAC wrapper or `GeneralsOnlineZH_60.exe`) |
| `将军无限` (GeneralsOnlineUnlimited) | 修改版 `GeneralsOnlineZH_Unlimited.exe`（无限制镜头，自动去除反作弊插件——EAC 拒绝修改过的可执行文件）/ Our modified unlimited client (anticheat plugin dropped, since EAC rejects modified executables) |

语言面板：当前语言的国旗会显示为中文国旗，点击它即可切换到中文界面；切换后该国旗恢复原样。/ The active language's flag displays the Chinese flag; clicking it switches the launcher to Chinese.

### 镜头控制 / Camera control
- 俯仰角与拉远高度滑块：原版模式写入 GenTool 的 `d3d8.cfg`（`[gentool76]` 节与平面键同步管理、去重、窗口预设固定 TOP），GO 模式写入客户端 `settings.json` 的 `camera` 对象。
- 游戏退出后自动从配置文件回读——游戏内 PageUp/PageDown 或 GenTool 的修改不会丢。
- / Pitch and zoom-out sliders write GenTool's `d3d8.cfg` (vanilla) or the client's `settings.json` (GO); values are re-read from the files whenever the game exits.

### 画质 / Quality
- MSAA+Filter 组合档位（关闭/2X/4X/8X），经 `Options.ini` 的 `AntiAliasing` 键生效；原版引擎忽略该键。
- 分辨率选择与 GO 启动参数（`-win -xres -yres`、`-disableCommunityDataPatch`）自动传递。
- / A combined MSAA + filter tier persisted through the `AntiAliasing` key the GO client reads; resolution and windowed arguments follow the launch.

### 自更新与安装修复 / Self-update & install repair
- 在线清单：启动器直接解析 `https://dl.mayeamiya.dev/index.html` 的下载链接（无需本地文件列表），按分组还原文件——`GeneralsOnlineUnlimited`（引擎 + 官方 GO 客户端）、`ContraXBeta2Patch1`（模组）、`GenTool_v8.9`（GenTool）。通过 HTTP HEAD 的 ETag + 大小比对版本（缓存在 `Contra_RemoteCache.txt`），文件缺失或上游更新时自动重新下载。
- 首次安装：无 `Contra_Installed.marker` 时要求目录干净（空，允许 `runtime_lib\`），目录不干净会双语提示并退出；完整安装成功后写入标记，之后的启动进入检查修复模式。
- 基础游戏文件（将军原版 / 绝命时刻）内嵌在启动器内，从注册表定位的本机安装修复：同盘优先硬链接，跨盘复制；清单来源完全缺失时报告错误。
- 运行库：启动器检测 VC++ 2015-2022 x86 与 legacy DirectX（d3dx9），缺失时优先使用本地 `runtime_lib\` 安装包，否则从微软官方链接下载安装。
- 更新渠道：Cloudflare R2 桶 `contrax-release`（`https://dl.mayeamiya.dev/`）。启动器读取桶根目录的 `Versions_X.txt`（`Launcher: x.y.z$...` 格式），有新版本时下载 `Contra_Launcher.zip` 解压并重启；MOTD（公告）同样来自该文件。桶内容未就绪时全部静默跳过。

### 其他 / Misc
- 简体中文界面自动跟随系统区域；GO 客户端 `settings.json` 首次启动自动生成；GenTool 相机写入兼容 7.6+ 布局。

## 使用 / Usage

把启动器放进游戏目录（绝命时刻主目录，Contra X 整合包根目录），直接运行 `Contra_Launcher_New.exe`。启动器会自动定位文档目录、生成缺失的 `Options.ini` / `settings.json`、校验安装文件，然后按下拉框选中的版本启动。

Place the launcher in the Zero Hour directory (the Contra X distribution root) and run it. It locates the Documents folder, generates missing `Options.ini` / `settings.json`, verifies the installation, then starts whatever the version dropdown selected.

## 构建 / Compiling

Requires Windows and one of the following SDKs:

**.NET 10** (netcore, single-file publish)

```
dotnet publish /p:Configuration=Release netcore/netcore.csproj
```

Build Location: `netcore\bin\Release\net10.0-windows\win-x86\publish\Contra_Launcher_New.exe`（自包含 win-x86 单文件，目标机无需安装 .NET 运行时 / self-contained single file, no runtime install needed）

**.NET Framework 4.8** (net48)

```
msbuild /p:Configuration=Release /p:Platform=AnyCPU net48/net48.csproj
```

Build Location: `net48\bin\Release\Contra_Launcher_New.exe`

用 Visual Studio 打开解决方案构建亦可 / Building from Visual Studio works as well.

## R2 桶文件布局 / Bucket layout (contrax-release)

```
Versions_X.txt                          # "Launcher: 2.0.0.8.C1$" + "Launcher-SHA256: <zip 哈希>$" + MOTD 行
<version>/Contra_Launcher.zip           # 自更新包：内含 Contra_Launcher_New_<version>.exe
GeneralsOnlineUnlimited/…               # 无限制引擎 + 官方 GO 客户端 exe
ContraXBeta2Patch1/…                    # Contra X Beta 2 模组文件
GenTool_v8.9/…                          # GenTool（d3d8.dll / ReadMe / links）
```

- 自更新：启动器比对 `Versions_X.txt` 中的版本与自身 `Application.ProductVersion`，不一致即下载对应版本目录的 `Contra_Launcher.zip`（包内 exe 必须命名为 `Contra_Launcher_New_<version>.exe`，解压后启动器自动换名重启）。**哈希校验**：`Versions_X.txt` 里写一行 `Launcher-SHA256: <Contra_Launcher.zip 的 SHA-256>$`，启动器下载后先验哈希，不符即删除并中止更新；不写该行则跳过校验（兼容官方格式）。
- 首次安装：启动器在无 `Contra_Installed.marker` 的目录中要求目录干净（空，允许 `runtime_lib\`），完整安装成功后才写入标记；之后的启动进入检查修复模式。
- `runtime_lib\`：可选。放入 `VC_redist.x86.exe` / `dxwebsetup.exe` 可离线安装运行库；否则启动器从微软官方链接下载。
- 同盘硬链接：ZH / Generals 文件修复优先创建硬链接（与源安装同卷时），不占额外空间。

## 致谢 / Credits

- [Contra Project Team](https://www.moddb.com/mods/contra) —— 原版启动器与 MOD（MIT，见 LICENSE.txt）
- [GeneralsGameCode / TheSuperHackers](https://github.com/TheSuperHackers/GeneralsGameCode) —— 开源引擎与安装注册表契约
- [Generals Online](https://generals.online/) —— 将军在线
