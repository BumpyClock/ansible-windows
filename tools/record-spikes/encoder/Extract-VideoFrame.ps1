[CmdletBinding()]
param([Parameter(Mandatory)][string]$Video, [Parameter(Mandatory)][string]$Image,
    [ValidateRange(0,300000)][int]$AtMilliseconds = 0)
$ErrorActionPreference = 'Stop'
$videoPath = [IO.Path]::GetFullPath($Video)
$imagePath = [IO.Path]::GetFullPath($Image)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$output = Join-Path $root '.runtime/record-spikes/encoder'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC x64 workload is required.' }
$msvc = Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk = Get-ChildItem (Join-Path $kits 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$compiler = Join-Path $msvc.FullName 'bin/Hostx64/x64/cl.exe'
$env:INCLUDE = (@((Join-Path $msvc.FullName 'include')) + @('ucrt','shared','um','winrt','cppwinrt' | ForEach-Object { Join-Path $sdk.FullName $_ })) -join ';'
$env:LIB = (@((Join-Path $msvc.FullName 'lib/x64')) + @('ucrt/x64','um/x64' | ForEach-Object { Join-Path (Join-Path $kits "Lib/$($sdk.Name)") $_ })) -join ';'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $output
try {
    & $compiler /nologo /std:c++20 /EHsc /O2 /MD (Join-Path $PSScriptRoot 'extract_frame.cpp') /Fe:extract_frame.exe /link windowsapp.lib
    if ($LASTEXITCODE -ne 0) { throw 'Frame extractor build failed.' }
    & (Join-Path $output 'extract_frame.exe') $videoPath $imagePath $AtMilliseconds
    if ($LASTEXITCODE -ne 0) { throw 'Frame extraction failed.' }
} finally { Pop-Location }
