[CmdletBinding()]
param([string]$OutputDirectory = "")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
$vswhere = Join-Path $vsInstaller "vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio with the Desktop development with C++ workload is required for NativeAOT publishing."
}
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) {
    throw "The MSVC x64 compiler workload is missing."
}
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
$project = Join-Path $root "DictationPoc\DictationPoc.csproj"
$env:PATH = "$vsInstaller;" + ($env:PATH -replace '"', '')
$outputArgument = ""
if ($OutputDirectory) {
    $directory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
    $outputArgument = " --output `"$directory`""
}
& $env:ComSpec /d /s /c "call `"$vcvars`" >nul && dotnet publish `"$project`" -c Release -r win-x64 -p:Platform=x64$outputArgument --nologo --verbosity minimal"
if ($LASTEXITCODE -ne 0) {
    throw "NativeAOT publish failed with exit code $LASTEXITCODE."
}
