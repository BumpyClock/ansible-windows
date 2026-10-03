[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "DictationPoc\DictationPoc.csproj"
$directory = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $root "DictationPoc\bin\AppPackages"
}
$directory = $directory.TrimEnd('\')

& (Join-Path $PSScriptRoot "Build-AudioNative.ps1") -StageRuntimeOnly
if ($LASTEXITCODE -ne 0) { throw "Native runtime staging failed." }
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
$vswhere = Join-Path $vsInstaller "vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio with the Desktop development with C++ workload is required for NativeAOT packaging."
}
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "The MSVC x64 compiler workload is missing." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
$msbuild = Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe"
$env:PATH = "$vsInstaller;" + ($env:PATH -replace '"', '')

& $env:ComSpec /d /s /c "call `"$vcvars`" >nul && `"$msbuild`" `"$project`" -restore -t:Build -p:Configuration=Release -p:Platform=x64 -p:PublishProfile=win-x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir=`"$directory\\`" -nologo -verbosity:minimal"
if ($LASTEXITCODE -ne 0) {
    throw "NativeAOT MSIX packaging failed with exit code $LASTEXITCODE."
}

[xml]$sourceManifest = Get-Content -LiteralPath (Join-Path $root "DictationPoc\Package.appxmanifest")
$identity = $sourceManifest.Package.Identity
$packageName = "DictationPoc_$($identity.Version)_x64"
$packagePath = Join-Path $directory "$($packageName)_Test\$packageName.msix"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "The build did not produce the expected package '$packagePath'."
}
$makepri = & $msbuild $project -p:Configuration=Release -p:Platform=x64 -getProperty:MakePriExeFullPath -nologo
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $makepri -PathType Leaf)) {
    throw "Cannot locate the project's MakePRI tool for packaged XAML verification."
}
$inspection = Join-Path $root ".runtime\package-validation\$([Guid]::NewGuid().ToString('N'))"
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $inspection)
    foreach ($resource in @(
        "AppxManifest.xml", "resources.pri", "DictationPoc.exe",
        "audiocpp.dll", "audio-models.json", "validation-sample.wav", "audio.cpp-LICENSE",
        "Assets\AppIcon.ico", "Assets\StoreLogo.png"
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $inspection $resource) -PathType Leaf)) {
            throw "The NativeAOT MSIX is missing '$resource'."
        }
    }
    if (Test-Path -LiteralPath (Join-Path $inspection "runtime-settings.json")) {
        throw "The package contains machine-specific development model settings."
    }
    $priDump = Join-Path $inspection "resources.xml"
    & $makepri dump /if (Join-Path $inspection "resources.pri") /of $priDump *> $null
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect the packaged WinUI resource index." }
    [xml]$resources = Get-Content -LiteralPath $priDump
    $resourceMap = $resources.PriInfo.ResourceMap | Where-Object name -EQ $identity.Name
    if (-not $resourceMap) { throw "The package resource index has no map for '$($identity.Name)'." }
    foreach ($xaml in @(
        "App.xbf", "MainWindow.xbf", "FloatingDictationWindow.xbf", "MainPage.xbf",
        "DictationPage.xbf", "InsightsPage.xbf", "ModelManagementPage.xbf"
    )) {
        # MSIX embeds compiled XAML in the PRI instead of shipping loose .xbf files.
        if (-not $resourceMap.SelectSingleNode(".//NamedResource[@name='$xaml']/Candidate[@type='EmbeddedData']")) {
            throw "The package resource index is missing embedded '$xaml'."
        }
    }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $inspection "AppxManifest.xml")
    if ($manifest.Package.Identity.Name -ne $identity.Name -or
        $manifest.Package.Identity.Publisher -ne $identity.Publisher -or
        $manifest.Package.Identity.Version -ne $identity.Version -or
        $manifest.Package.Identity.ProcessorArchitecture -ne "x64" -or
        $manifest.Package.Applications.Application.Executable -ne "DictationPoc.exe") {
        throw "The generated package identity or executable does not match the project manifest."
    }
    $stream = [IO.File]::OpenRead((Join-Path $inspection "DictationPoc.exe"))
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if ($pe.PEHeaders.CorHeader -or
            $pe.PEHeaders.CoffHeader.Machine -ne [Reflection.PortableExecutable.Machine]::Amd64) {
            throw "The packaged executable is not a native x64 PE image."
        }
    }
    finally {
        $pe.Dispose()
        $stream.Dispose()
    }
    foreach ($managedRuntime in @("DictationPoc.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll")) {
        if (Test-Path -LiteralPath (Join-Path $inspection $managedRuntime)) {
            throw "The NativeAOT MSIX unexpectedly contains '$managedRuntime'."
        }
    }
    & (Join-Path $PSScriptRoot "Build-AudioNative.ps1") -VerifyRuntimeOnly -NativeDirectory $inspection
    if ($LASTEXITCODE -ne 0) { throw "Packaged native import closure verification failed." }
}
finally {
    if (Test-Path -LiteralPath $inspection) {
        Remove-Item -LiteralPath $inspection -Recurse -Force
    }
}
Write-Host "Verified NativeAOT MSIX: $packagePath"
Write-Host "The package is unsigned. Use Visual Studio's packaged launch profile for development, or sign it before sideloading."
Write-Host "Associate the project with the Microsoft Store before creating a Store submission."
