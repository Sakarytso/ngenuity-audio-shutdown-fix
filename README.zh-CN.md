# NGENUITY 3.2.0 8 声道失声与关机报错修复

[English](README.md)

这是针对 HyperX NGENUITY 3.2.0 特定 Windows 构建的非官方补丁。它会重试飓风 3 无线（Cloud III Wireless）的输出绑定，并阻止 NGENUITY 在退出期间重新启动子进程。

## 方法 1｜解压后双击安装

1. 下载 [`NGENUITY-3.2.0-Quick-Install.zip`](https://github.com/imba1367954987/ngenuity-audio-shutdown-fix/releases/latest/download/NGENUITY-3.2.0-Quick-Install.zip)。
2. 右键 ZIP，选 **全部解压缩**，打开解压后的文件夹。
3. 双击 **`Install-Patch.cmd`**，等窗口显示 **安装完成**。安装会重开 NGENUITY，声音会短暂中断。

安装包适用于文件指纹匹配的 NGENUITY 3.2.0。安装器会联网下载并校验构建工具，再用本机原件生成补丁 DLL。如果需要下载便携版 PowerShell，下载量约为 105 MB。安装器会校验生成的文件并保留 HP 签名原件。想撤回时，双击同一文件夹里的 **`Restore-HP-original.cmd`**。请等固件更新完成后再安装。

如果 PowerShell 提示下载的脚本被阻止，请先检查压缩包和其中的文件。确认信任下载后，如果 ZIP 的 **属性** 页面显示 **解除锁定**，可以勾选后重新解压。[微软关于下载文件的说明](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/unblock-file)

## 故障现象

- **8 声道虚拟输出在所有应用中失声。** 切换到 Cloud III Wireless 实体耳机输出后立即有声。单独重启 NGENUITY 也能恢复虚拟输出。
- **Windows 关机时反复出现应用程序错误。** 本机有一次关机产生 16 条 `hxn-srv-audio-engine.exe` 的 `0xc0000142`（`STATUS_DLL_INIT_FAILED`）弹窗事件。NGENUITY 日志显示，程序开始退出后，音频引擎仍被重新拉起。[微软 NTSTATUS 文档](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55)

以下日志摘录已省去时间戳和机器标识。

```text
Target audio endpoint found. FriendlyName: [Cloud III Wireless]
Received Error message, action="SetOutputDeviceName", message=Failed to set output device: SetOutputDeviceName failed with code -8
Received Error message, action="SetChatOutputDeviceName", message=Failed to set chat output device: SetOutputDeviceName failed with code -8

Start App Shutdown...
AudioEngineManager StopAsync()
hxn-srv-audio-engine started successfully.
AudioEngineManager StopAsync()
hxn-srv-audio-engine started successfully.
```

## 补丁改动

1. **输出绑定。** 原版托管 DLL 收到绑定失败回复时只写日志。补丁调用原有请求接口，对同一副耳机的主输出和聊天输出各最多重试三次。每次收到失败回复后，分别等待 0.3、1、2 秒再发下一次请求。收到成功回复、切换输出设备、引擎断连或开始退出时，会取消对应的待执行重试。总恢复时间还取决于音频引擎的响应速度。
2. **关机流程。** 原版启动器可能在 NGENUITY 退出期间重新拉起已退出的子进程，主窗口也会把系统退出请求当作平时的关闭到托盘。补丁让应用或系统发起的退出通过窗口关闭流程，保留固件更新状态检查，并只进入一次原有清理流程。子进程启动入口和实际创建进程前都会检查退出状态，覆盖等待后继续运行的路径。清理期间，已断开的 NetMQ 连接按已关闭处理。[Windows 退出消息文档](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession)

补丁替换的是用户态托管模块 `NGENUITY.dll`。生成的 DLL 没有 HP 发布者签名，安装时会保留可回滚的 HP 签名原件。原生音频引擎、音效插件、Windows 驱动和耳机固件均在补丁范围之外。

## 验证与边界

- 在发生故障的 Windows 11 电脑上，通过了 IL 校验和行为测试。在模拟连续失败时，测得三次等待约为 307、1000、2000 毫秒。测试也覆盖了两种 `-8` 回复格式、取消重试、重复断开 IPC，以及应用退出时阻止子进程启动。
- 在该电脑上单独向 NGENUITY 窗口发送 Windows 会话退出请求。应用和子进程均退出，观察期间没有新的子进程启动或弹窗。测试没有执行整机关机。
- 重开打补丁后的软件，耳机主输出和聊天输出均绑定成功。向 8 声道端点播放测试音时，虚拟端点和实体耳机的音量表都测到了信号。
- 本次测试期间未观察到自然复发后的自动恢复，也没有验证实际整机关机。另有一条原生引擎堆损坏事件 `0xc0000374`，原因尚未确定。

## 方法 2｜用源码生成补丁

准备 Windows x64、运行在 .NET 10 上的 PowerShell 7.6.9、[Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6)，以及与脚本文件指纹匹配的 HyperX NGENUITY 3.2.0 原版安装。相同的版本号也可能对应不同的文件。脚本会校验原始 DLL、协议 DLL、启动程序及生成补丁的 SHA-256，任一不匹配都会停止。

在本仓库目录内打开 PowerShell 7.6.9，运行以下命令。

```powershell
$ErrorActionPreference = 'Stop'
$install = Join-Path $env:LOCALAPPDATA 'NGENUITY\current'
$work = Join-Path $PWD '.build'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$package = Join-Path $work 'Mono.Cecil.0.11.6.zip'
Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/mono.cecil/0.11.6/mono.cecil.0.11.6.nupkg' -OutFile $package
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne 'D2A23832AAA948BA9A01ACC42B5726E34C5F995958F1B30D45C0E7C70B3A72D5') {
    throw 'Unexpected Mono.Cecil package hash.'
}
$cecil = Join-Path $work 'cecil'
Expand-Archive -LiteralPath $package -DestinationPath $cecil -Force
$candidate = Join-Path $work 'NGENUITY.patched.dll'
.\Build.ps1 -OriginalDirectory $install -CecilDirectory (Join-Path $cecil 'lib\netstandard2.0') -Destination $candidate
```

已验证的原版 `NGENUITY.dll` SHA-256 为 `78048CDFBF5F5A4321425A047F47C267883FCEBC4ADEF791A941E3292FE1F334`。已验证的补丁 DLL SHA-256 为 `62987D4345083218F93236143BE5618C81271D54440E5A0CF2E98BAC8F405B9B`。方法 1 也会从签名原件生成同一份补丁。如果构建版本或编译器生成了不同的指纹，请使用匹配的构建。

## 安装与回滚

请在固件更新完成后操作。安装会重启 NGENUITY，播放将短暂中断。

```powershell
.\Switch-Version.ps1 -Mode Status -InstallDirectory $install
.\Switch-Version.ps1 -Mode Apply -InstallDirectory $install -CandidatePath $candidate
.\Switch-Version.ps1 -Mode Status -InstallDirectory $install
```

确认 `Patched` 和 `BackupValid` 都为真。启动成功时，NGENUITY 日志会出现 `[LocalAudioPatch] NGENUITY 3.2.0 local repair loaded.`。若无法确认打补丁后的软件启动，安装脚本会恢复已签名的原件。

回滚请运行 `.\Switch-Version.ps1 -Mode Restore -InstallDirectory $install`。如果使用默认安装路径，也可以双击 `Restore-HP-original.cmd`。原件备份位于 `current` 的上一级目录，文件名为 `NGENUITY.3.2.0.HP-original.dll`。官方软件更新可能会覆盖补丁。升级后应先核对厂商发布说明和文件版本。

之前采用的重启办法见 [B 站教程](https://www.bilibili.com/video/BV1NzK56vERo/)。
