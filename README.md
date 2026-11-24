# NGENUITY 3.2.0 — 8-channel silence and shutdown errors

[简体中文](README.zh-CN.md)

An unofficial patch for a specific Windows build of HyperX NGENUITY 3.2.0. It retries a failed output binding for the Cloud III Wireless and prevents NGENUITY from relaunching its helper processes while exiting.

## Method 1 — Double-click installer

1. Download [`NGENUITY-3.2.0-Quick-Install.zip`](https://github.com/imba1367954987/ngenuity-audio-shutdown-fix/releases/latest/download/NGENUITY-3.2.0-Quick-Install.zip).
2. Right-click the ZIP, choose **Extract All**, and open the extracted folder.
3. Double-click **`Install-Patch.cmd`**. Wait until it says **Patch installed**. NGENUITY restarts, so playback briefly stops.

The installer downloads and verifies its build tools, then generates the DLL from your matching NGENUITY 3.2.0 installation. It may need to download about 105 MB for portable PowerShell. It checks the generated file and keeps the signed original. To undo the change, double-click **`Restore-HP-original.cmd`** in the same folder. Finish firmware updates before installing.

If PowerShell reports that a downloaded script is blocked, review the ZIP and its contents first. If you trust the download and Windows shows an **Unblock** checkbox in the ZIP's **Properties**, select it there and extract the ZIP again. [Microsoft's downloaded-file guidance](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/unblock-file)

## What happened

- **8-channel playback went silent across applications.** Selecting the physical Cloud III Wireless output immediately restored sound. Restarting NGENUITY alone also restored the virtual route.
- **Windows displayed repeated error dialogs during shutdown.** One observed shutdown produced 16 application-popup events for `hxn-srv-audio-engine.exe` with `0xc0000142` (`STATUS_DLL_INIT_FAILED`). The NGENUITY log recorded new audio-engine launches after shutdown had already begun. [Microsoft NTSTATUS reference](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/596a1078-e883-4972-9bbc-49e60bebca55)

These log excerpts omit timestamps and machine identifiers.

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

## What the patch changes

1. **Output binding.** In the stock managed DLL, the error reply was logged without scheduling another bind. The patch uses the existing request path to retry the same selected headset up to three times per failed main or chat binding. After each failure, it waits 0.3, 1, and 2 seconds respectively before sending the next request. A successful response, a device change, a disconnected engine, or shutdown cancels the relevant pending retry. The audio engine's response time affects the total recovery time.
2. **Shutdown.** The stock launcher could restart a child after it exited during application teardown. The main window also applied its ordinary close-to-tray behavior to a system exit request. The patch lets an application or OS exit proceed through that window, retains its existing firmware-update check, and enters the existing teardown once. It checks for shutdown at the launcher entry points and again before starting a process, including after asynchronous waits. An already-disconnected NetMQ endpoint is treated as closed during cleanup. [Windows shutdown messages](https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession)

Only the managed `NGENUITY.dll` is replaced. The resulting file lacks HP's publisher signature. The original signed file is saved for rollback. The native audio engine, its plug-in, the Windows driver, and headset firmware are outside the patch.

## Validation and limits

- On the affected Windows 11 PC, IL verification and behavioral checks passed. In a simulated series of failures, the three delays measured about 307, 1000, and 2000 ms. Tests also covered both `-8` reply formats, cancellation, an already-disconnected IPC endpoint, and blocking new launches during application exit.
- A Windows session-ending request was sent to NGENUITY's window on that PC. The application and its helpers exited without a new helper launch or popup during the observation window. The test did not shut down Windows.
- After restarting the patched app, the headset's main and chat bindings succeeded. A test tone submitted to the 8-channel endpoint produced signal at both the virtual endpoint and the physical headset's meter.
- Automatic recovery from a naturally recurring dropout and a full Windows shutdown were not observed in these tests. A separate native engine heap-corruption event (`0xc0000374`) remains unexplained.

## Method 2 — Build from source

Use Windows x64, PowerShell 7.6.9 on .NET 10, [Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6), and a stock HyperX NGENUITY 3.2.0 installation that matches the fingerprints in the scripts. An application with the same version label can still have different files. The installer checks the exact SHA-256 of the original DLL, its protocol DLL, the launcher, and the generated patch. A mismatch stops the operation.

Run the following commands in PowerShell 7.6.9 from this repository's directory.

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

The tested original `NGENUITY.dll` hashes to `78048CDFBF5F5A4321425A047F47C267883FCEBC4ADEF791A941E3292FE1F334`. The tested candidate hashes to `62987D4345083218F93236143BE5618C81271D54440E5A0CF2E98BAC8F405B9B`. The quick installer builds the same candidate from the signed original. If a build or a compiler produces a different hash, use a matching build.

## Install and restore

Finish any firmware update before continuing. Applying the patch briefly stops playback while NGENUITY restarts.

```powershell
.\Switch-Version.ps1 -Mode Status -InstallDirectory $install
.\Switch-Version.ps1 -Mode Apply -InstallDirectory $install -CandidatePath $candidate
.\Switch-Version.ps1 -Mode Status -InstallDirectory $install
```

Confirm that `Patched` and `BackupValid` are true. A successful start includes `[LocalAudioPatch] NGENUITY 3.2.0 local repair loaded.` in NGENUITY's log. The installer restores the signed backup if it cannot verify patched startup.

To roll back, run `.\Switch-Version.ps1 -Mode Restore -InstallDirectory $install` or open `Restore-HP-original.cmd` for the default installation path. The signed backup is stored as `NGENUITY.3.2.0.HP-original.dll` in the parent of the `current` directory. A vendor update can replace the patched file. Check the vendor's release before trying this patch on a newer build.

For the original restart workaround, see the [Bilibili tutorial](https://www.bilibili.com/video/BV1NzK56vERo/).
