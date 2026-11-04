[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$project = Join-Path $PSScriptRoot 'ClipboardSpike.csproj'
$output = Join-Path $root '.runtime/record-spikes/clipboard-app'
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'
$vswhere = Join-Path $installer 'vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Desktop development with C++ is required.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'The MSVC x64 compiler workload is missing.' }
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$env:PATH = "$installer;" + ($env:PATH -replace '"', '')
& $env:ComSpec /d /s /c "call `"$vcvars`" >nul && dotnet publish `"$project`" -c Release -o `"$output`" --verbosity minimal"
if ($LASTEXITCODE -ne 0) { throw "Clipboard spike NativeAOT publish failed with exit code $LASTEXITCODE." }

$executable = Join-Path $output 'ClipboardSpike.exe'
$stream = [IO.File]::OpenRead($executable)
$pe = [Reflection.PortableExecutable.PEReader]::new($stream)
try {
    if ($pe.PEHeaders.CorHeader -or $pe.PEHeaders.CoffHeader.Machine -ne [Reflection.PortableExecutable.Machine]::Amd64) {
        throw 'The clipboard harness is not a native x64 executable.'
    }
}
finally { $pe.Dispose(); $stream.Dispose() }
Write-Host "Verified NativeAOT harness $executable"
