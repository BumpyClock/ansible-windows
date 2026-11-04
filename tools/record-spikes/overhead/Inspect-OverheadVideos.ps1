[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$folder = [IO.Path]::GetFullPath($RunDirectory)
$output = Join-Path $root '.runtime/record-spikes/overhead-app'
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'
$vswhere = Join-Path $installer 'vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'The MSVC x64 compiler workload is required.' }
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$env:PATH = "$installer;" + ($env:PATH -replace '"', '')
$tool = Join-Path $output 'video_info.exe'
$source = Join-Path $PSScriptRoot 'video_info.cpp'
& $env:ComSpec /d /s /c "call `"$vcvars`" >nul && cl.exe /nologo /std:c++20 /EHsc /O2 /MD `"$source`" /Fe:`"$tool`" /Fo:`"$output\video_info.obj`" /link windowsapp.lib"
if ($LASTEXITCODE -ne 0) { throw 'Video metadata tool build failed.' }
$metadata = foreach ($file in Get-ChildItem -LiteralPath $folder -Filter *.mp4) {
    $line = & $tool $file.FullName
    if ($LASTEXITCODE -ne 0 -or $line -notmatch '^width=(\d+) height=(\d+) duration100ns=(\d+) bitrate=(\d+)$') {
        throw "Video metadata inspection failed for $($file.Name)."
    }
    [pscustomobject]@{ File=$file.Name; Width=[int]$Matches[1]; Height=[int]$Matches[2]; Duration100ns=[long]$Matches[3]; Bitrate=[long]$Matches[4]; Bytes=$file.Length }
}
$metadata | Export-Csv -LiteralPath (Join-Path $folder 'video-metadata.csv') -NoTypeInformation
$metadata
