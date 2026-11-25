# WPF 加密视频播放器

.NET 8 WPF x64，要求 Windows 10 2004 或更高版本。开发者将 MP4/MKV 转为 `.svideo`，按账号授予播放权限。播放器内置登录、设备绑定、暂停、拖动、循环和 Windows 录屏防护。

## 本机启动

本机项目已初始化时，双击 `Start-Player.cmd` 启动播放器和本机授权服务。脚本首次在全新环境运行时会提示创建管理员。播放和管理都从播放器窗口进入：

- 登录后可直接播放本地 MP4/MKV。加密视频（`.svideo`）归属加密时的管理员账号，该管理员可播放、导出和授权，普通成员获得该视频授权后可播放。
- 旧版数据库中没有加密人记录的 `.svideo` 会提示「权限不足」。用对应的管理员账号重新加密原视频，再为成员授权。
- 管理员联网登录后会看到「管理员工具」，可以加密视频、解密导出原格式、创建普通成员和按视频授权。登录前、普通成员登录、离线登录和退出后会隐藏工具栏。
- 加密文件默认保存到 Windows 的「视频」文件夹。可在管理员工具中选择其他文件夹，或点「默认」恢复。输出沿用原视频文件名并改用 `.svideo` 扩展名。同名文件会保留。
- 创建成员时可设置账号有效期（1–3650 天）、设备上限（1–20 台），以及加密视频是否允许离线播放。给成员分配视频时可另设有效天数，留空时随账号有效期。已有成员和视频授权沿用原有设置。正常播放不会写出明文文件，管理员主动选择解密导出时会写出明文文件。

安装 .NET 8 Desktop Runtime 和 ASP.NET Core Runtime。开发或首次构建还需 .NET SDK。如需创建独立的测试账号和示例视频，在 PowerShell 中运行：

```powershell
.\scripts\Start-LocalDemo.ps1
```

首次运行会提示给本机 `demo` 账号设置密码，并生成一段测试视频。脚本启动授权服务和播放器，在终端显示加密文件的路径。登录后选择该文件。用自己的视频测试可传 `-Source 'D:\视频\输入.mkv'`。本机数据存放于 `artifacts/local-demo`，授权端口为 `127.0.0.1:5185`。

## 播放列表

登录后可添加一个或多个视频，也可加载整个文件夹。文件夹中的 MP4、MKV 和 `.svideo` 会从子文件夹一起加入。列表按文件名开头的数字从小到大排序，无数字开头的文件按名称排在后面。双击列表项，或选中后点「播放所选视频」，即可切换播放。长文件名和完整路径可以用列表底部的横向滚动条查看。

登录页会填入本机上次成功登录的账号，密码仍需输入。使用本机授权配置时，直接打开播放器也会在登录前尝试启动当前项目的授权服务。

## ZIP 播放

在 Windows 点「打开 ZIP」后，播放器只读取压缩包目录，将包内子目录中的视频连同路径加入右侧列表。双击条目或选中后点「播放所选视频」才会开始读取内容。支持普通 ZIP 中的 MP4、MKV 和 `.svideo`，其中 `.svideo` 仍需账号授权。视音频数据不会完整载入内存，也不会解压到磁盘。压缩条目的定位可能需要重新读取经过的内容，拖到大视频的较远位置可能较慢。带密码的 ZIP、RAR 和 7z 不在本版支持范围内。

## Android

安装 `artifacts/SecureVideo-Android.apk`。适用于 Android 8.0 或更新版本的 ARM64 手机和 x64 模拟器。点击「添加视频 / ZIP」，可直接播放手机内的 MP4/MKV，或先列出 ZIP 条目再点击播放。编码支持取决于设备的 Android 解码器。

播放 `.svideo` 时，在手机上点击「授权设置」导入 Windows 服务端通过 `export-config` 导出的 `player.json`，然后登录已有成员账号。配置中的 HTTPS 地址须能从手机访问。电脑本机的 `127.0.0.1` 配置不能用于手机。手机登录计入设备上限。管理员联网登录并在当前设备取得自己视频的许可后，可以离线不限时播放。普通成员离线时仍受账号、视频和离线有效期限制。管理和加密功能由 Windows 版提供。

开发构建使用 .NET 9 Android 工作负载、JDK 17 和 Android SDK API 35。安装好工具后运行 `dotnet build Mobile/Mobile.csproj -c Release`，签名 APK 在 `Mobile/bin/Release/net9.0-android`。Windows 上的 Android 原生构建工具如无法识别中文路径，可使用纯英文路径或目录联接。

## 播放快捷键

| 快捷键 | 操作 |
| --- | --- |
| 空格 / K | 播放或暂停 |
| ← / → | 后退或前进 5 秒 |
| ↑ / ↓ | 调整音量，每次 5% |
| M | 静音或取消静音 |
| F / F11，或双击画面 | 切换全屏 |
| Esc | 退出全屏 |
| `[` / `]` | 降低或提高倍速 |
| R | 恢复 1× |
| Home | 回到视频开头 |
| S | 停止 |
| Ctrl+O | 打开视频 |

播放时鼠标静止 3 秒会隐藏进度条和控制按钮，移动鼠标即可显示。暂停、拖动进度条或打开倍速菜单时控制栏保持可见。

底部菜单提供 0.5×、0.75×、1×、1.25×、1.5×、1.75× 和 2×。变速时保留原音高。

## 开发者管理

开发者在授权服务器上操作。用同一个 Windows 用户运行管理命令和授权服务，DPAPI 将加密库绑定到这个用户。将库目录设置为仅该 Windows 用户及系统管理员可访问的专用位置。分发给成员时，授权服务应运行在开发者控制的主机上，成员电脑只安装播放器。

```powershell
.\scripts\Install-FFmpeg.ps1
dotnet build SecureVideo.sln -c Release

New-Item -ItemType Directory -Path .\artifacts -Force | Out-Null
$env:SVIDEO_VAULT = 'D:\SecureVideoVault'
$server = '.\Server\bin\Release\net8.0-windows\Server.exe'
& $server init
& $server add-user user01 30 1
& $server add-admin admin01 3650 1
$media = & $server import admin01 'D:\视频\输入.mp4' 'D:\视频\输出.svideo'
& $server grant user01 $media
& $server export-config 'https://video.example.com:7443' '.\artifacts\player.json'
```

`add-user` 依次接收账号、有效期天数和设备上限，然后在终端读取初始密码。其他管理命令为 `disable-user ACCOUNT`、`renew-user ACCOUNT DAYS`、`reset-devices ACCOUNT` 和 `revoke ACCOUNT MEDIA_ID`。原始视频由开发者保管。播放器 API 不返回用户列表。加密登记、导出、创建成员和分配权限由服务端校验管理员角色和设备签名。

正式服务使用受信任的 HTTPS 证书。以下示例用 7443，也可以选其他端口。导出配置时使用电脑和手机都能访问的 HTTPS 地址及对应端口。Windows 可把导出的文件放到 Player.exe 旁边命名为 `player.json`，或传给 `Start-Player.cmd`。Android 可从「授权设置」导入同一份文件。通过运行服务的 Windows 用户安全配置证书密码。

```powershell
$env:ASPNETCORE_Kestrel__Certificates__Default__Path = 'D:\证书\video.pfx'
& $server serve --urls 'https://0.0.0.0:7443'
```

用导出的配置发布客户端。构建将服务器公钥和 FFmpeg 文件哈希嵌入播放器，使用 Obfuscar 混淆托管代码并隐藏字符串。提供 Windows SDK 的 SignTool 路径和代码签名证书指纹后，脚本会签名并验证应用组件。发布目录中的 `player` 才是给观众的文件，无需打包授权服务器或其数据库。播放器内的 FFmpeg 为 LGPL 动态库，发行时保留许可证和对应的源代码获取信息。

```powershell
.\scripts\Publish-Player.ps1 -Config '.\artifacts\player.json'
# 签名参数  -SignTool <signtool.exe 路径> -CertificateThumbprint <指纹>
```

FFmpeg 来源：[固定版本的构建和许可证信息](https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/tag/autobuild-2026-11-24-12-41)，[对应的 FFmpeg 源码](https://github.com/FFmpeg/FFmpeg/tree/a8029b6528)。

## 授权和录屏边界

- 每部视频有独立的 AES-256-GCM 内容密钥。许可签名绑定账号、设备、视频和到期时间。服务器为设备公钥封装该片的密钥，客户端没有全库密钥。续发许可证更新授权时限，同一密文使用同一内容密钥。
- 在线播放期间会定期复查权限。普通成员获准离线时，最多可播到最后一次授权后一小时，且受账号、视频和会话到期限制。管理员在当前设备取得自己视频的许可后可离线不限时播放，离线登录仍需密码，管理功能仍需联网。长期离线设备必须重新联系服务端才会收到撤权结果。
- FFmpeg 从认证过的内存流读取。应用不创建明文视频文件。播放后清空可控缓冲区，Windows 分页、系统转储、原生解码器缓冲以及管理员级内存访问仍由运行环境控制。
- 使用 `WDA_EXCLUDEFROMCAPTURE` 将播放器窗口从受支持的 Windows 捕获路径中排除。保护不可用时停止播放。外部相机、采集卡和高权限采集仍可能取得画面。混淆和完整性校验可以增加逆向成本，已取得本机控制权的攻击者仍可能从播放进程提取密钥或画面。

## 验证

```powershell
dotnet build SecureVideo.sln -c Release
dotnet run --project tests/Smoke/Smoke.csproj -c Release -- (Get-Location).Path
```

将环境变量 `SVIDEO_TEST_PUBLISH` 设为 `1` 可额外用混淆后的发布包验证同一套 WPF 登录和播放流程。

测试在交互式 Windows 桌面运行，使用临时授权库和动态分配的本机端口。它生成合成的 MP4/MKV，验证容器篡改和截断、认证解码与拖动、在线登录、断网缓存、恢复联网后撤销授权，以及管理员导出还原、普通成员越权拒绝、两种角色的面板可见性和本地视频播放。
