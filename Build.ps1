[CmdletBinding()]
param(
    [string]$OriginalDirectory = (Join-Path $env:LOCALAPPDATA 'NGENUITY\current'),
    [string]$OriginalAssembly,
    [Parameter(Mandatory)][string]$CecilDirectory,
    [Parameter(Mandatory)][string]$Destination
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7 -or [Environment]::Version.Major -lt 10) {
    throw 'Build with PowerShell 7 running on .NET 10 or later.'
}
$cecil = Join-Path (Resolve-Path -LiteralPath $CecilDirectory).Path 'Mono.Cecil.dll'
$rocks = Join-Path (Split-Path -Parent $cecil) 'Mono.Cecil.Rocks.dll'
$source = (Resolve-Path -LiteralPath $OriginalDirectory).Path
$originalDll = Join-Path $source 'NGENUITY.dll'
if ($OriginalAssembly) { $originalDll = (Resolve-Path -LiteralPath $OriginalAssembly).Path }
$destinationPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
if ([string]::Equals($destinationPath,(Join-Path $source 'NGENUITY.dll'),[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose a staging file outside the original installation.'
}
if (Test-Path -LiteralPath $destinationPath) {
    throw 'The staging file already exists. Choose an unused destination.'
}
Add-Type -Path @($cecil,$rocks)
$frameworkRefs = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' |
    Select-Object -ExpandProperty FullName)
if (-not ('NgInternalPatch' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'BuildPatch.cs') -ReferencedAssemblies (@($cecil,$rocks) + $frameworkRefs)
}
$helper = Join-Path ([IO.Path]::GetTempPath()) ('ngenuity-helper-' + [guid]::NewGuid().ToString('N') + '.dll')
try {
    Add-Type -Path (Join-Path $PSScriptRoot 'Recovery.cs') -OutputAssembly $helper
    [NgInternalPatch]::Build($originalDll, (Join-Path $source 'NGENUITY.Common.dll'),
                             $helper, $destinationPath)
    Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256
} finally {
    if (Test-Path -LiteralPath $helper) { Remove-Item -LiteralPath $helper }
}
