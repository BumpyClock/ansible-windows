[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$Architecture = $Architecture.ToLowerInvariant()
$output = Join-Path $root $(if ($Architecture -eq 'x64') { '.runtime/record-spikes/encoder' } else { '.runtime/record-spikes/encoder-arm64' })
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$env:PATH = (Split-Path $vswhere -Parent) + ';' + ($env:PATH -replace '"', '')
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC x64 workload is required.' }
$msvc = Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk = Get-ChildItem (Join-Path $kits 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$compiler = Join-Path $msvc.FullName "bin/Hostx64/$Architecture/cl.exe"
if (-not (Test-Path -LiteralPath $compiler)) { throw "Install the Visual Studio C++ $Architecture build tools." }
$env:INCLUDE = (@((Join-Path $msvc.FullName 'include')) + @('ucrt','shared','um','winrt','cppwinrt' | ForEach-Object { Join-Path $sdk.FullName $_ })) -join ';'
$env:LIB = (@((Join-Path $msvc.FullName "lib/$Architecture")) + @("ucrt/$Architecture","um/$Architecture" | ForEach-Object { Join-Path (Join-Path $kits "Lib/$($sdk.Name)") $_ })) -join ';'
$source = Join-Path $PSScriptRoot 'record_encoder.cpp'
$hostSource = Join-Path $PSScriptRoot 'host.cpp'
Push-Location $output
try {
    & $compiler /nologo /std:c++20 /EHsc /O2 /MD /LD $source /Fe:record_encoder_spike.dll /link windowsapp.lib d3d11.lib dxgi.lib mfplat.lib mfreadwrite.lib mfuuid.lib ole32.lib user32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Encoder helper build failed.' }
    & $compiler /nologo /std:c++20 /EHsc /O2 /MD $hostSource record_encoder_spike.lib /Fe:record_encoder_host.exe
    if ($LASTEXITCODE -ne 0) { throw 'Encoder host build failed.' }
} finally { Pop-Location }
Write-Output (Join-Path $output 'record_encoder_spike.dll')
