[CmdletBinding()]
param(
    [ValidateSet('Status','Apply','Restore')][string]$Mode = 'Status',
    [string]$CandidatePath,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'NGENUITY\current')
)

# Check the installed DLL or switch between a verified patch and the signed backup.
$ErrorActionPreference = 'Stop'
$originalHash = '78048CDFBF5F5A4321425A047F47C267883FCEBC4ADEF791A941E3292FE1F334'
$patchedHash = '62987D4345083218F93236143BE5618C81271D54440E5A0CF2E98BAC8F405B9B'
$previousPatchHashes = @(
    '088C8489296053C271B6C717B5FCB39271D62A12CE0DC270361EC5875CF545DD',
    '72DAB32FD4F06D0019C9DD6A10F36225A2144427402820D97CB24B9FB23BC7D2'
)
$commonHash = 'D4EAE861D1D1F2596CD9B1C41401BE653F60B72094284AA9E724D8E75708C453'
$exeHash = '1C0012C0658E0AD81346BFF2EF24C65B93F3C78D433520DC742201F957BF58A8'
$directory = (Resolve-Path -LiteralPath $InstallDirectory).Path
$parent = Split-Path -Parent $directory
$dll = Join-Path $directory 'NGENUITY.dll'
$exe = Join-Path $directory 'NGENUITY.exe'
$backup = Join-Path $parent 'NGENUITY.3.2.0.HP-original.dll'
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId

function Read-Hash([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-TargetProcess {
    Get-Process -Name 'NGENUITY' -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq $session -and $_.Path -eq $exe
    }
}

function Stop-Target {
    $targets = @(Get-TargetProcess)
    if ($targets.Count -gt 1) { throw 'Multiple matching processes were found.' }
    if ($targets.Count -eq 0) { return }
    $target = $targets[0]
    if ($target.HasExited) { return }
    & (Join-Path $env:SystemRoot 'System32\taskkill.exe') /PID $target.Id /T /F | Out-Null
    if ($LASTEXITCODE -ne 0 -and -not $target.HasExited) { throw 'Could not stop NGENUITY.' }
    Start-Sleep -Seconds 2
    if (@(Get-TargetProcess).Count -gt 0) { throw 'Another NGENUITY instance started. No DLL change made.' }
}

function Use-Assembly([string]$Source, [string]$ExpectedActive, [string]$ExpectedSource) {
    if ((Read-Hash $dll) -ne $ExpectedActive -or (Read-Hash $Source) -ne $ExpectedSource) {
        throw 'The current installation or replacement no longer matches the expected hash.'
    }
    $staged = Join-Path $directory ('NGENUITY.dll.swap-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::Copy($Source, $staged, $false)
        if ((Read-Hash $staged) -ne $ExpectedSource) { throw 'Replacement copy failed verification.' }
        [IO.File]::Replace($staged, $dll, [Management.Automation.Language.NullString]::Value)
        if ((Read-Hash $dll) -ne $ExpectedSource) { throw 'Installed DLL failed verification.' }
    } finally {
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged }
    }
}

function Open-Target {
    Start-Process -FilePath $exe -WorkingDirectory $directory -WindowStyle Hidden -PassThru
}

function Wait-ForStartup($Process, [datetime]$StartedAfter, [bool]$Patched) {
    $until = [datetime]::Now.AddSeconds(24)
    do {
        if ($Process.HasExited) { throw "NGENUITY exited during startup (exit code $($Process.ExitCode))." }
        $path = Join-Path $parent ('Logs\NGENUITY-log-' + (Get-Date -Format yyyyMMdd) + '.txt')
        if (Test-Path -LiteralPath $path) {
            $ready = $false
            $engine = $false
            foreach ($line in (Get-Content -LiteralPath $path -Tail 350)) {
                if ($line -notmatch '^\[(?<time>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) ') { continue }
                $at = [datetime]::ParseExact($Matches.time,'yyyy-MM-dd HH:mm:ss.fff',[cultureinfo]::InvariantCulture)
                if ($at -lt $StartedAfter) { continue }
                if ($line.Contains('[LocalAudioPatch] NGENUITY 3.2.0 local repair loaded.')) { $ready = $true }
                if ($line.Contains('hxn-srv-audio-engine started successfully.')) { $engine = $true }
            }
            if ($engine -and ($ready -or -not $Patched)) { return }
        }
        Start-Sleep -Milliseconds 700
    } while ([datetime]::Now -lt $until)
    throw 'NGENUITY or its audio engine did not report startup in time.'
}

$active = Read-Hash $dll
if ($Mode -eq 'Status') {
    [pscustomobject]@{ActiveHash=$active; Original=($active -eq $originalHash); Patched=($active -eq $patchedHash)
        PreviousPatch=($active -in $previousPatchHashes)
        BackupValid=((Test-Path -LiteralPath $backup) -and (Read-Hash $backup) -eq $originalHash)}
    return
}
if ((Read-Hash (Join-Path $directory 'NGENUITY.Common.dll')) -ne $commonHash -or (Read-Hash $exe) -ne $exeHash) {
    throw 'This command applies only to the tested NGENUITY 3.2.0 installation.'
}
$updater = @(Get-Process -Name 'Update' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $parent 'Update.exe') })
if ($updater.Count -gt 0) { throw 'NGENUITY is updating. Retry after the update has finished.' }

if ($Mode -eq 'Restore') {
    if ($active -eq $originalHash) { 'The original HP DLL is already installed.'; return }
    if (($active -ne $patchedHash -and $active -notin $previousPatchHashes) -or
        -not (Test-Path -LiteralPath $backup) -or (Read-Hash $backup) -ne $originalHash) {
        throw 'The active DLL or signed backup does not match the tested files. Nothing was replaced.'
    }
    Stop-Target
    Use-Assembly $backup $active $originalHash
    $startedAfter = [datetime]::Now
    $process = Open-Target
    Wait-ForStartup $process $startedAfter $false
    'Original HP DLL restored. NGENUITY audio engine started.'
    return
}

if ($active -eq $patchedHash) { 'This patch is already installed.'; return }
if ($active -ne $originalHash -and $active -notin $previousPatchHashes) {
    throw 'The active DLL does not match the supported 3.2.0 builds.'
}
if (-not $CandidatePath) { throw 'Specify the verified staging DLL using -CandidatePath.' }
$candidate = (Resolve-Path -LiteralPath $CandidatePath).Path
if ((Read-Hash $candidate) -ne $patchedHash) { throw 'The staging DLL does not match the tested patch.' }
if (-not (Test-Path -LiteralPath $backup)) {
    if ($active -ne $originalHash) { throw 'The signed backup is missing.' }
    [IO.File]::Copy($dll, $backup, $false)
}
if ((Read-Hash $backup) -ne $originalHash -or (Get-AuthenticodeSignature -LiteralPath $backup).Status -ne 'Valid') {
    throw 'The HP original backup could not be verified.'
}

Stop-Target
try {
    Use-Assembly $candidate $active $patchedHash
    $startedAfter = [datetime]::Now
    $process = Open-Target
    Wait-ForStartup $process $startedAfter $true
    'Local DLL patch loaded. NGENUITY audio engine started.'
} catch {
    $problem = $_.Exception.Message
    if ((Read-Hash $dll) -eq $patchedHash) {
        Stop-Target
        Use-Assembly $backup $patchedHash $originalHash
    }
    if ((Read-Hash $dll) -eq $originalHash -and @(Get-TargetProcess).Count -eq 0) { $null = Open-Target }
    throw "Patch could not be verified: $problem. Check -Mode Status for the active version."
}
