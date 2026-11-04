[CmdletBinding()]
param([switch]$TestHost)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $root '.runtime/record-capture'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$env:PATH = (Split-Path $vswhere -Parent) + ';' + ($env:PATH -replace '"', '')
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC x64 workload is required.' }
$msvc = Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk = Get-ChildItem (Join-Path $kits 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$compiler = Join-Path $msvc.FullName 'bin/Hostx64/x64/cl.exe'
$env:INCLUDE = (@((Join-Path $msvc.FullName 'include')) + @('ucrt','shared','um','winrt','cppwinrt' | ForEach-Object { Join-Path $sdk.FullName $_ })) -join ';'
$env:LIB = (@((Join-Path $msvc.FullName 'lib/x64')) + @('ucrt/x64','um/x64' | ForEach-Object { Join-Path (Join-Path $kits "Lib/$($sdk.Name)") $_ })) -join ';'
$source = Join-Path $PSScriptRoot 'record_capture.cpp'
Push-Location $output
try {
    & $compiler /nologo /std:c++20 /EHsc /O2 /MD /DUNICODE /D_UNICODE /LD $source /Fe:record_capture.dll /link windowsapp.lib d3d11.lib dxgi.lib mfplat.lib mfreadwrite.lib mfuuid.lib ole32.lib user32.lib gdi32.lib gdiplus.lib dwmapi.lib
    if ($LASTEXITCODE -ne 0) { throw 'Record capture helper build failed.' }
    if ($TestHost) {
        $encoder = Join-Path $root '.runtime/record-spikes/encoder'
        if (-not (Test-Path -LiteralPath (Join-Path $encoder 'record_encoder_spike.lib'))) { throw 'Build the encoder spike before the native test host.' }
        & $compiler /nologo /std:c++20 /EHsc /O2 /MD /DUNICODE /D_UNICODE (Join-Path $PSScriptRoot 'host.cpp') record_capture.lib (Join-Path $encoder 'record_encoder_spike.lib') /Fe:record_capture_host.exe /link user32.lib
        if ($LASTEXITCODE -ne 0) { throw 'Record capture test host build failed.' }
        & $compiler /nologo /std:c++20 /EHsc /O2 /MD /DUNICODE /D_UNICODE (Join-Path $PSScriptRoot 'fixture_host.cpp') (Join-Path $encoder 'record_encoder_spike.lib') /Fe:record_capture_fixture.exe /link windowsapp.lib d3d11.lib dxgi.lib mfplat.lib mfreadwrite.lib mfuuid.lib ole32.lib user32.lib gdi32.lib gdiplus.lib dwmapi.lib
        if ($LASTEXITCODE -ne 0) { throw 'Record capture fixture build failed.' }
        Copy-Item -LiteralPath (Join-Path $encoder 'record_encoder_spike.dll') -Destination $output -Force
    }
} finally { Pop-Location }
Write-Output (Join-Path $output 'record_capture.dll')
