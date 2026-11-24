# NGENUITY 3.2.0 8 声道失声与关机报错修复

[English](README.md)

这是针对 Windows x64 版 HyperX NGENUITY 3.2.0 特定构建的非官方补丁，已在飓风 3 无线（Cloud III Wireless）上测试。

## 故障现象

- **8 声道失声。** 使用 NGENUITY 虚拟输出的应用没声，切到耳机直连仍能播放。日志显示软件已识别耳机，但主输出和聊天输出的绑定都返回了 `-8`。重启 NGENUITY 后声音恢复。[原排查视频](https://www.bilibili.com/video/BV1NzK56vERo/)
- **关机反复弹窗。** 本机有一次关机产生了 16 条 `hxn-srv-audio-engine.exe` 的 `0xc0000142` 错误。NGENUITY 日志显示，软件开始退出后仍在重新启动音频引擎。[微软错误码文档](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55)

## 补丁改动

- **输出绑定。** 主输出或聊天输出收到 `-8` 后，对同一副耳机最多重试三次。连续失败时，每次收到错误回复后依次等待 **0.3、1、2 秒**再重试。成功后取消重试，实际恢复时间还取决于音频引擎的响应速度。
- **关机流程。** 固件未在更新时，主窗口允许系统退出。应用开始退出后只启动一次清理流程，处理已经断开的 IPC 连接，并停止重新启动子进程。[Windows 退出消息文档](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession)

补丁在本机生成未签名的托管模块 `NGENUITY.dll`，替换前会保留 HP 签名原件，供回滚使用。

## 验证与边界

- **在发生故障的 Windows 11 电脑上复测。** 实际整机关机未再出现音频引擎报错弹窗。正常使用中 8 声道失声再次发生时，播放也能自动恢复。
- 在 Windows 11 上通过了 IL 校验。模拟连续返回 `-8` 时，从失败到重试测得约 307、1000、2000 毫秒。测试也覆盖了取消和重试上限。
- 单独向 NGENUITY 窗口发送模拟的 Windows 退出通知后，应用和子进程均退出。测试期间没有重新拉起子进程或出现新弹窗。
- 重开软件后，主输出和聊天输出成功绑定。向 8 声道虚拟设备播放测试音时，虚拟输出和实体耳机的音量表均测到信号。
- 另有一条原生音频引擎崩溃记录 `0xc0000374`，超出本补丁的修复范围。

## 方法 1｜双击安装

1. [下载 v1.0.0 安装包（ZIP）](https://github.com/Sakarytso/ngenuity-audio-shutdown-fix/releases/download/v1.0.0/NGENUITY-3.2.0-Quick-Install.zip)，右键选择 **全部解压缩**。
2. 打开解压后的文件夹，双击 **`Install-Patch.cmd`**，等窗口显示 **安装完成**。

请先完成固件更新。安装器会核对软件的文件指纹，并重开 NGENUITY，声音会短暂中断。首次运行需要联网下载并校验构建工具。如果安装器无法复用匹配的 PowerShell，就会下载约 110 MB 的便携版。

如果 PowerShell 阻止运行解压后的脚本，请先核对来源和文件内容。确认信任后，如 ZIP 的 **属性** 页中有 **解除锁定**，勾选并重新解压。[微软关于下载文件的说明](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/unblock-file)

## 方法 2｜从源码构建

需要 PowerShell 7.6.9、[Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6)，以及符合[构建脚本](BuildPatch.cs)和[安装脚本](Switch-Version.ps1)中 SHA-256 校验要求的原版 NGENUITY。完成固件更新后，在仓库目录运行以下命令。

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
.\Switch-Version.ps1 -Mode Apply -InstallDirectory $install -CandidatePath $candidate
.\Switch-Version.ps1 -Mode Status -InstallDirectory $install
```

安装后，`Patched` 和 `BackupValid` 应均为 `True`。若无法验证补丁启动，安装器会尝试恢复原件。

## 回滚

使用默认安装路径时，双击 `Restore-HP-original.cmd` 即可。使用自定义路径时，把 `$install` 设为该路径，再运行 `.\Switch-Version.ps1 -Mode Restore -InstallDirectory $install`。签名原件 `NGENUITY.3.2.0.HP-original.dll` 备份在 `current` 的上一级目录。官方更新可能会覆盖补丁。
