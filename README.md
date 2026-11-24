# NGENUITY 3.2.0 — 8-channel audio and shutdown fix

[简体中文](README.zh-CN.md)

An unofficial patch for a specific Windows x64 build of HyperX NGENUITY 3.2.0, tested with the Cloud III Wireless.

## What happened

- **8-channel audio stopped.** Apps using NGENUITY's virtual output went silent while direct headset output still worked. The log showed the headset was detected, but `SetOutputDeviceName` and `SetChatOutputDeviceName` returned `-8`. Restarting NGENUITY restored playback. [Original troubleshooting video](https://www.bilibili.com/video/BV1NzK56vERo/)
- **Repeated shutdown popups.** On one shutdown, Windows recorded 16 `0xc0000142` errors for `hxn-srv-audio-engine.exe`. The NGENUITY log showed the app relaunching the audio engine after it began shutting down. [Microsoft NTSTATUS reference](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55)

## What the patch changes

- **Output binding.** The app retries the same headset up to three times per main or chat output after a `-8` reply. It schedules the attempts **0.3, 1, and 2 seconds** after successive failures. A successful reply clears the retry. The audio engine's response time also affects recovery.
- **Shutdown.** The main window lets a system exit proceed when no firmware update is running. Once app exit begins, cleanup starts once, already closed IPC connections are handled, and the app stops launching helper processes. [Windows shutdown messages](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession)

The patch replaces the managed `NGENUITY.dll` with a locally built unsigned DLL and saves the HP-signed original for rollback.

## Validation and limits

- On Windows 11, IL verification passed. Tests with simulated `-8` replies measured about 307, 1000, and 2000 ms between each failure and its retry. They also covered cancellation and the retry limit.
- In an app-only test, a simulated Windows exit notification closed NGENUITY and its helpers. No helper relaunches or Windows popups were recorded during that test.
- After reopening the patched app, the main and chat outputs bound successfully. A test tone registered on the meters for both the 8-channel virtual output and the physical headset.
- Recovery from a naturally recurring dropout and a full Windows shutdown remain unverified. A separately observed native engine crash with `0xc0000374` is outside the scope of this patch.

## Method 1 — Quick install

1. [Download the v1.0.0 installer (ZIP)](https://github.com/Sakarytso/ngenuity-audio-shutdown-fix/releases/download/v1.0.0/NGENUITY-3.2.0-Quick-Install.zip) and choose **Extract All**.
2. In the extracted folder, double-click **`Install-Patch.cmd`** and wait for **Patch installed**.

Finish any firmware update first. The installer checks the exact build and restarts NGENUITY, briefly interrupting playback. It needs internet access to download verified build tools. If the installer cannot reuse a matching PowerShell installation, it downloads the portable version (about 110 MB).

If PowerShell blocks scripts extracted from the download, review the ZIP and its contents. If you trust the download and the ZIP's **Properties** shows **Unblock**, select it and extract the ZIP again. [Microsoft download guidance](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/unblock-file)

## Method 2 — Build from source

Use PowerShell 7.6.9, [Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6), and an original NGENUITY installation that matches the SHA-256 checks in [the builder](BuildPatch.cs) and [the installer](Switch-Version.ps1). Run these commands from the repository folder after any firmware update has finished.

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

After installation, `Patched` and `BackupValid` should both be true. If the installer cannot verify patched startup, it attempts to restore the original.

## Rollback

Double-click `Restore-HP-original.cmd` for the default installation path. For a custom path, set `$install` to that path and run `.\Switch-Version.ps1 -Mode Restore -InstallDirectory $install`. The signed backup is named `NGENUITY.3.2.0.HP-original.dll` and is stored one folder above `current`. A vendor update may replace the patch.
