[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [ValidateSet("cpu", "vulkan")]
    [string]$Backend = "vulkan",
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    [switch]$Bundle
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "Ansible\Ansible.csproj"
$Architecture = $Architecture.ToLowerInvariant()
$platform = if ($Architecture -eq "arm64") { "ARM64" } else { "x64" }
$machine = if ($Architecture -eq "arm64") { [Reflection.PortableExecutable.Machine]::Arm64 } else { [Reflection.PortableExecutable.Machine]::Amd64 }
if ($Architecture -eq "arm64" -and $Backend -ne "cpu") {
    throw "ARM64 currently supports CPU only. Pass -Architecture arm64 -Backend cpu."
}
$directory = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $root "Ansible\bin\AppPackages"
}
$directory = $directory.TrimEnd('\')

& (Join-Path $PSScriptRoot "Build-AudioNative.ps1") -StageRuntimeOnly -Backend $Backend -Architecture $Architecture
if ($LASTEXITCODE -ne 0) { throw "Native runtime staging failed." }
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
$vswhere = Join-Path $vsInstaller "vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio with the Desktop development with C++ workload is required for NativeAOT packaging."
}
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "The MSVC x64 compiler workload is missing." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvarsall.bat"
$vcTarget = if ($Architecture -eq "arm64") { "x64_arm64" } else { "x64" }
$msbuild = Join-Path $vs "MSBuild\Current\Bin\amd64\MSBuild.exe"
$env:PATH = "$vsInstaller;" + ($env:PATH -replace '"', '')

& $env:ComSpec /d /s /c "call `"$vcvars`" $vcTarget >nul && `"$msbuild`" `"$project`" -restore -t:Build -p:Configuration=Release -p:Platform=$platform -p:RuntimeIdentifier=win-$Architecture -p:PublishProfile=win-$Architecture -p:AppxBundle=Never -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir=`"$directory\\`" -nodeReuse:false -nologo -verbosity:minimal"
if ($LASTEXITCODE -ne 0) {
    throw "NativeAOT MSIX packaging failed with exit code $LASTEXITCODE."
}

[xml]$sourceManifest = Get-Content -LiteralPath (Join-Path $root "Ansible\Package.appxmanifest")
$identity = $sourceManifest.Package.Identity
$packageName = "Ansible_$($identity.Version)_$Architecture"
$packagePath = Join-Path $directory "$($packageName)_Test\$packageName.msix"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "The build did not produce the expected package '$packagePath'."
}
$makepri = & $msbuild $project -p:Configuration=Release -p:Platform=$platform -getProperty:MakePriExeFullPath -nologo
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $makepri -PathType Leaf)) {
    throw "Cannot locate the project's MakePRI tool for packaged XAML verification."
}
$inspection = Join-Path $root ".runtime\package-validation\$([Guid]::NewGuid().ToString('N'))"
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $inspection)
    foreach ($resource in @(
        "AppxManifest.xml", "resources.pri", "Ansible.exe", "DeviceProbe\Ansible.DeviceProbe.exe",
        "audiocpp.dll", "record_capture.dll", "audio-models.json", "validation-sample.wav", "audio.cpp-LICENSE",
        "Assets\AppIcon.ico", "Assets\StoreLogo.png",
        "Assets\ModelIcons\moonshine.png", "Assets\ModelIcons\moonshine-dark.png",
        "Assets\ModelIcons\qwen.png", "Assets\ModelIcons\qwen-dark.png",
        "Assets\ModelIcons\vibevoice.png", "Assets\ModelIcons\vibevoice-dark.png",
        "Assets\ModelIcons\nvidia.png", "Assets\ModelIcons\nvidia-dark.png",
        "Assets\ModelIcons\NOTICE.txt", "Assets\ModelIcons\moonshine-LICENSE.txt",
        "Assets\ModelIcons\qwen-LICENSE.txt", "Assets\ModelIcons\vibevoice-LICENSE.txt"
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
    # Every XAML file in the app compiles to an embedded .xbf; the list comes from source so it cannot drift.
    $compiledXaml = @(Get-ChildItem -LiteralPath (Join-Path $root "Ansible") -Filter *.xaml | ForEach-Object { "$($_.BaseName).xbf" })
    if ($compiledXaml.Count -eq 0) { throw "No app XAML files were found to verify in the package." }
    foreach ($xaml in $compiledXaml) {
        # MSIX embeds compiled XAML in the PRI instead of shipping loose .xbf files.
        if (-not $resourceMap.SelectSingleNode(".//NamedResource[@name='$xaml']/Candidate[@type='EmbeddedData']")) {
            throw "The package resource index is missing embedded '$xaml'."
        }
    }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $inspection "AppxManifest.xml")
    if ($manifest.Package.Identity.Name -ne $identity.Name -or
        $manifest.Package.Identity.Publisher -ne $identity.Publisher -or
        $manifest.Package.Identity.Version -ne $identity.Version -or
        $manifest.Package.Identity.ProcessorArchitecture -ne $Architecture -or
        $manifest.Package.Applications.Application.Executable -ne "Ansible.exe") {
        throw "The generated package identity or executable does not match the project manifest."
    }
    $nativeImages = @("Ansible.exe", "DeviceProbe\Ansible.DeviceProbe.exe", "record_capture.dll")
    if (Test-Path -LiteralPath (Join-Path $inspection "record_encoder_spike.dll")) {
        $nativeImages += "record_encoder_spike.dll"
    }
    foreach ($executable in $nativeImages) {
        $stream = [IO.File]::OpenRead((Join-Path $inspection $executable))
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if ($pe.PEHeaders.CorHeader -or
                $pe.PEHeaders.CoffHeader.Machine -ne $machine) {
                throw "The packaged image '$executable' is not a native $Architecture PE image."
            }
        }
        finally {
            $pe.Dispose()
            $stream.Dispose()
        }
    }
    foreach ($managedRuntime in @(
        "Ansible.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
        "DeviceProbe\Ansible.DeviceProbe.dll", "DeviceProbe\coreclr.dll",
        "DeviceProbe\hostfxr.dll", "DeviceProbe\hostpolicy.dll"
    )) {
        if (Test-Path -LiteralPath (Join-Path $inspection $managedRuntime)) {
            throw "The NativeAOT MSIX unexpectedly contains '$managedRuntime'."
        }
    }
    & (Join-Path $PSScriptRoot "Build-AudioNative.ps1") -VerifyRuntimeOnly -NativeDirectory $inspection -Backend $Backend -Architecture $Architecture
    if ($LASTEXITCODE -ne 0) { throw "Packaged native import closure verification failed." }
}
finally {
    if (Test-Path -LiteralPath $inspection) {
        Remove-Item -LiteralPath $inspection -Recurse -Force
    }
}
Write-Host "Verified NativeAOT MSIX: $packagePath"
if ($Bundle) {
    $makeappx = Join-Path (Split-Path $makepri -Parent) "makeappx.exe"
    if (-not (Test-Path -LiteralPath $makeappx -PathType Leaf)) { throw "MakeAppx is missing beside MakePRI." }
    $bundlePath = Join-Path $directory "$packageName.msixbundle"
    $staging = Join-Path $root ".runtime\package-validation\$([Guid]::NewGuid().ToString('N'))"
    try {
        $inputDirectory = New-Item -ItemType Directory -Path (Join-Path $staging "input") -Force
        Copy-Item -LiteralPath $packagePath -Destination $inputDirectory.FullName
        & $makeappx bundle /d $inputDirectory.FullName /p $bundlePath /bv $identity.Version /o
        if ($LASTEXITCODE -ne 0) { throw "MSIX bundle creation failed." }
        $unpacked = Join-Path $staging "unpacked"
        [IO.Compression.ZipFile]::ExtractToDirectory($bundlePath, $unpacked)
        [xml]$bundleManifest = Get-Content -LiteralPath (Join-Path $unpacked "AppxMetadata\AppxBundleManifest.xml")
        $bundleIdentity = $bundleManifest.Bundle.Identity
        $applications = @($bundleManifest.Bundle.Packages.Package | Where-Object Type -EQ "application")
        if ($bundleIdentity.Name -ne $identity.Name -or $bundleIdentity.Publisher -ne $identity.Publisher -or
            $bundleIdentity.Version -ne $identity.Version -or $applications.Count -ne 1 -or
            $applications[0].Architecture -ne $Architecture -or $applications[0].FileName -ne "$packageName.msix") {
            throw "The bundle identity or architecture does not match the verified package."
        }
        $embedded = Join-Path $unpacked "$packageName.msix"
        if ((Get-FileHash -LiteralPath $embedded -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash) {
            throw "The bundled MSIX differs from the verified package."
        }
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
    Write-Host "Verified NativeAOT MSIX bundle: $bundlePath"
}
Write-Host "The package is unsigned. Use Visual Studio's packaged launch profile for development, or sign it before sideloading."
Write-Host "Associate the project with the Microsoft Store before creating a Store submission."
