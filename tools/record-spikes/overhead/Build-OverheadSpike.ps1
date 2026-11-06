[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$project = Join-Path $PSScriptRoot 'OverheadSpike.csproj'
$output = Join-Path $root '.runtime/record-spikes/overhead-app'
$artifacts = Join-Path $root '.runtime/record-spikes/overhead-build'
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'
$vswhere = Join-Path $installer 'vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'The MSVC x64 compiler workload is required.' }
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$env:PATH = "$installer;" + ($env:PATH -replace '"', '')
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
& $env:ComSpec /d /s /c "call `"$vcvars`" >nul && dotnet publish `"$project`" -c Release --artifacts-path `"$artifacts`" -o `"$output`" --verbosity minimal"
if ($LASTEXITCODE -ne 0) { throw "Overhead harness publish failed with exit code $LASTEXITCODE." }
$stream = [IO.File]::OpenRead((Join-Path $output 'OverheadSpike.exe'))
$pe = [Reflection.PortableExecutable.PEReader]::new($stream)
try {
    if ($pe.PEHeaders.CorHeader -or $pe.PEHeaders.CoffHeader.Machine -ne [Reflection.PortableExecutable.Machine]::Amd64) {
        throw 'The overhead harness is not a native x64 executable.'
    }
} finally { $pe.Dispose(); $stream.Dispose() }
Write-Output "Verified NativeAOT overhead harness $output"
