[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [string]$ModelsDirectory = "",
    [switch]$CleanDeployment
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if ($CleanDeployment -and $ModelsDirectory) {
    throw "CleanDeployment and ModelsDirectory cannot be combined."
}
if (-not $CleanDeployment) {
    $ModelsDirectory = if ($ModelsDirectory) {
        [IO.Path]::GetFullPath($ModelsDirectory)
    } else {
        Join-Path $root ".runtime\models"
    }
    if (-not (Test-Path -LiteralPath $ModelsDirectory -PathType Container)) {
        throw "The development model directory does not exist. Specify -ModelsDirectory or use -CleanDeployment."
    }
}

function Write-DeploymentSettings {
    param([string]$Directory, [string]$ModelsDirectory, [switch]$CleanDeployment)
    $settings = Join-Path $Directory "runtime-settings.json"
    if ($CleanDeployment) {
        if (Test-Path -LiteralPath $settings) {
            throw "Clean deployment requires an output directory without runtime-settings.json. Choose a fresh output directory."
        }
        return
    }
    if (-not (Test-Path -LiteralPath $ModelsDirectory -PathType Container)) {
        throw "The configured model directory does not exist."
    }
    [ordered]@{ models_directory = [IO.Path]::GetFullPath($ModelsDirectory) } |
        ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding utf8
}

& (Join-Path $PSScriptRoot "Build-AudioNative.ps1") -StageRuntimeOnly
if ($LASTEXITCODE -ne 0) { throw "Native runtime staging failed." }
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
else {
    $directory = Join-Path $root "DictationPoc\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish"
    $outputArgument = " --output `"$directory`""
}
if ($CleanDeployment -and (Test-Path -LiteralPath (Join-Path $directory "runtime-settings.json"))) {
    throw "Clean deployment requires an output directory without runtime-settings.json. Choose a fresh output directory."
}
& $env:ComSpec /d /s /c "call `"$vcvars`" >nul && dotnet publish `"$project`" -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=true -p:PublishAot=true -p:WindowsPackageType=None$outputArgument --nologo --verbosity minimal"
if ($LASTEXITCODE -ne 0) {
    throw "NativeAOT publish failed with exit code $LASTEXITCODE."
}
& (Join-Path $PSScriptRoot "Build-AudioNative.ps1") -VerifyRuntimeOnly -NativeDirectory $directory
if ($LASTEXITCODE -ne 0) { throw "Published native import closure verification failed." }
foreach ($resource in @("DictationPoc.exe", "DictationPoc.pri", "MainWindow.xbf")) {
    if (-not (Test-Path -LiteralPath (Join-Path $directory $resource))) {
        throw "The NativeAOT publication is missing '$resource'."
    }
}
Write-DeploymentSettings -Directory $directory -ModelsDirectory $ModelsDirectory -CleanDeployment:$CleanDeployment
