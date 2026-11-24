[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'NGENUITY\current'),
    [switch]$VerifyOnly
)

# Download verified build tools and use the local original DLL to prepare the patch for installation.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$switch = Join-Path $PSScriptRoot 'Switch-Version.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')
$runDir = Join-Path $tempRoot ('NGENUITY-LocalPatch-' + [guid]::NewGuid().ToString('N'))

function Get-VerifiedDownload([string]$Url, [string]$Destination, [string]$Sha256) {
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Destination -TimeoutSec 180
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) {
        throw "下载文件校验失败 / Download verification failed: $(Split-Path -Leaf $Destination)"
    }
}

try {
    if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
        throw '需要 x64 Windows / x64 Windows is required.'
    }
    $install = (Resolve-Path -LiteralPath $InstallDirectory).Path
    $status = & $switch -Mode Status -InstallDirectory $install
    if ($status.Patched -and -not $VerifyOnly) {
        Write-Host '新版补丁已经安装 / This patch is already installed.'
        return
    }
    if ($status.Original) {
        $original = Join-Path $install 'NGENUITY.dll'
    } elseif (($status.PreviousPatch -or $status.Patched) -and $status.BackupValid) {
        $original = Join-Path (Split-Path -Parent $install) 'NGENUITY.3.2.0.HP-original.dll'
    } else {
        throw '仅支持文件指纹匹配的 NGENUITY 3.2.0 / This NGENUITY build is unsupported.'
    }

    [void](New-Item -ItemType Directory -Path $runDir)
    $pwsh = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
    $matchingPwsh = $false
    if (Test-Path -LiteralPath $pwsh) {
        $signature = Get-AuthenticodeSignature -LiteralPath $pwsh
        if ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Subject -match 'Microsoft Corporation') {
            $version = & $pwsh -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()'
            $matchingPwsh = $LASTEXITCODE -eq 0 -and $version -eq '7.6.9'
        }
    }
    if (-not $matchingPwsh) {
        Write-Host '正在下载微软便携版 PowerShell（约 105 MB） / Downloading Microsoft PowerShell (about 105 MB)…'
        $psArchive = Join-Path $runDir 'PowerShell-7.6.9-win-x64.zip'
        Get-VerifiedDownload 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.9/PowerShell-7.6.9-win-x64.zip' $psArchive 'CE7484804C0F0AD4DDA10C958E71ACDAF7EEBFB22505C1B480F89E0E9F1DA94D'
        $runtime = Join-Path $runDir 'powershell'
        Expand-Archive -LiteralPath $psArchive -DestinationPath $runtime
        $pwsh = Join-Path $runtime 'pwsh.exe'
    }

    Write-Host '正在下载并校验构建依赖 / Downloading and verifying the build dependency…'
    $cecilArchive = Join-Path $runDir 'Mono.Cecil.0.11.6.zip'
    Get-VerifiedDownload 'https://api.nuget.org/v3-flatcontainer/mono.cecil/0.11.6/mono.cecil.0.11.6.nupkg' $cecilArchive 'D2A23832AAA948BA9A01ACC42B5726E34C5F995958F1B30D45C0E7C70B3A72D5'
    $cecil = Join-Path $runDir 'cecil'
    Expand-Archive -LiteralPath $cecilArchive -DestinationPath $cecil

    Write-Host '正在从本机原件生成补丁 / Building the patch from the local original…'
    $candidate = Join-Path $runDir 'NGENUITY.patched.dll'
    & $pwsh -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File (Join-Path $PSScriptRoot 'Build.ps1') -OriginalDirectory $install -OriginalAssembly $original -CecilDirectory (Join-Path $cecil 'lib\netstandard2.0') -Destination $candidate
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $candidate) -or
        (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne '62987D4345083218F93236143BE5618C81271D54440E5A0CF2E98BAC8F405B9B') {
        throw '生成的补丁校验失败 / The generated patch did not match the tested build.'
    }
    if ($VerifyOnly) {
        Write-Host '补丁校验通过 / Local build verified.'
        return
    }
    Write-Host '正在替换文件并重开 NGENUITY / Applying the patch and restarting NGENUITY…'
    & $switch -Mode Apply -InstallDirectory $install -CandidatePath $candidate
    Write-Host '安装完成 / Patch installed.' -ForegroundColor Green
} catch {
    Write-Host '安装未完成 / Installation did not complete.' -ForegroundColor Red
    Write-Host $_.Exception.Message
    exit 1
} finally {
    if (Test-Path -LiteralPath $runDir) {
        $folder = Get-Item -LiteralPath $runDir -Force
        if ($folder.FullName -eq $runDir -and $folder.Parent.FullName -eq $tempRoot -and
            -not ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $runDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
