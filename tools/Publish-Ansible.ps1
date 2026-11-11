[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [ValidateSet("cpu", "vulkan")]
    [string]$Backend = "vulkan",
    [ValidateSet("x64", "arm64")]
    [string[]]$Architecture = @("x64"),
    [switch]$Bundle
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "Ansible\Ansible.csproj"
$requestedArchitectures = @($Architecture | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
if ($requestedArchitectures.Count -gt 1 -and -not $Bundle) { throw "Pass -Bundle when selecting multiple architectures." }
$directory = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $root "Ansible\bin\AppPackages"
}
$directory = $directory.TrimEnd('\')
[xml]$sourceManifest = Get-Content -LiteralPath (Join-Path $root "Ansible\Package.appxmanifest")
$identity = $sourceManifest.Package.Identity
$verifiedPackages = [Collections.Generic.List[object]]::new()

function Remove-ValidationDirectory {
    param([string]$Path)
    $target = [IO.Path]::GetFullPath($Path)
    $validationRoot = [IO.Path]::GetFullPath((Join-Path $root ".runtime\package-validation")) + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($validationRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing package inspection cleanup outside $validationRoot."
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}

function Build-VerifiedPackage {
    param([string]$Architecture)
    $platform = if ($Architecture -eq "arm64") { "ARM64" } else { "x64" }
    $machine = if ($Architecture -eq "arm64") { [Reflection.PortableExecutable.Machine]::Arm64 } else { [Reflection.PortableExecutable.Machine]::Amd64 }

    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    $vswhere = Join-Path $vsInstaller "vswhere.exe"
    [string[]]$vcComponents = @("Microsoft.VisualStudio.Component.VC.Tools.x86.x64")
    if ($Architecture -eq "arm64") { $vcComponents += "Microsoft.VisualStudio.Component.VC.Tools.ARM64" }
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "Visual Studio Installer vswhere.exe is missing. Required for $Architecture native builds and packaging with MSVC components: $($vcComponents -join ', ')."
    }
    $vs = & $vswhere -latest -products * -requires $vcComponents -property installationPath
    if (-not $vs) { throw "Visual Studio with MSVC components '$($vcComponents -join "', '")' is required for $Architecture native builds and packaging." }
    $vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvarsall.bat"
    $vcTarget = if ($Architecture -eq "arm64") { "x64_arm64" } else { "x64" }
    $msbuild = Join-Path $vs "MSBuild\Current\Bin\amd64\MSBuild.exe"

    & (Join-Path $PSScriptRoot "Build-AudioNative.ps1") -StageRuntimeOnly -Backend $Backend -Architecture $Architecture
    if ($LASTEXITCODE -ne 0) { throw "Native runtime staging failed." }
    $env:PATH = "$vsInstaller;" + ($env:PATH -replace '"', '')

    & $env:ComSpec /d /s /c "call `"$vcvars`" $vcTarget >nul && `"$msbuild`" `"$project`" -restore -t:Build -p:Configuration=Release -p:Platform=$platform -p:RuntimeIdentifier=win-$Architecture -p:PublishProfile=win-$Architecture -p:AppxBundle=Never -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir=`"$directory\\`" -nodeReuse:false -nologo -verbosity:minimal"
    if ($LASTEXITCODE -ne 0) {
        throw "NativeAOT MSIX packaging failed with exit code $LASTEXITCODE."
    }

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
            "Assets\ModelIcons\moonshine.png",
            "Assets\ModelIcons\qwen.png",
            "Assets\ModelIcons\vibevoice.png", "Assets\ModelIcons\vibevoice-dark.png",
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
        Remove-ValidationDirectory $inspection
    }
    Write-Host "Verified NativeAOT MSIX: $packagePath"
    $verifiedPackages.Add([pscustomobject]@{ Architecture = $Architecture; Name = $packageName; Path = $packagePath; MakePri = $makepri })
}

# Builds share restore state, so finish and verify each architecture before starting the next.
foreach ($requestedArchitecture in $requestedArchitectures) { Build-VerifiedPackage $requestedArchitecture }
if ($Bundle) {
    $makeappx = Join-Path (Split-Path $verifiedPackages[0].MakePri -Parent) "makeappx.exe"
    if (-not (Test-Path -LiteralPath $makeappx -PathType Leaf)) { throw "MakeAppx is missing beside MakePRI." }
    $bundleName = "Ansible_$($identity.Version)_$($requestedArchitectures -join '_')"
    $bundlePath = Join-Path $directory "$bundleName.msixbundle"
    $staging = Join-Path $root ".runtime\package-validation\$([Guid]::NewGuid().ToString('N'))"
    try {
        $inputDirectory = New-Item -ItemType Directory -Path (Join-Path $staging "input") -Force
        foreach ($package in $verifiedPackages) { Copy-Item -LiteralPath $package.Path -Destination $inputDirectory.FullName }
        & $makeappx bundle /d $inputDirectory.FullName /p $bundlePath /bv $identity.Version /o
        if ($LASTEXITCODE -ne 0) { throw "MSIX bundle creation failed." }
        $unpacked = Join-Path $staging "unpacked"
        [IO.Compression.ZipFile]::ExtractToDirectory($bundlePath, $unpacked)
        [xml]$bundleManifest = Get-Content -LiteralPath (Join-Path $unpacked "AppxMetadata\AppxBundleManifest.xml")
        $bundleIdentity = $bundleManifest.Bundle.Identity
        $applications = @($bundleManifest.Bundle.Packages.Package | Where-Object Type -EQ "application")
        if ($bundleIdentity.Name -ne $identity.Name -or $bundleIdentity.Publisher -ne $identity.Publisher -or
            $bundleIdentity.Version -ne $identity.Version -or $applications.Count -ne $verifiedPackages.Count) {
            throw "The bundle identity or architecture count does not match the verified packages."
        }
        foreach ($package in $verifiedPackages) {
            $application = @($applications | Where-Object Architecture -EQ $package.Architecture)
            if ($application.Count -ne 1 -or $application[0].FileName -ne "$($package.Name).msix") {
                throw "The bundle does not contain the expected $($package.Architecture) package."
            }
            $embedded = Join-Path $unpacked $application[0].FileName
            if ((Get-FileHash -LiteralPath $embedded -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $package.Path -Algorithm SHA256).Hash) {
                throw "The bundled $($package.Architecture) MSIX differs from the verified package."
            }
        }
    }
    finally {
        Remove-ValidationDirectory $staging
    }
    Write-Host "Verified NativeAOT MSIX bundle: $bundlePath"
}
Write-Host "The package is unsigned. Use Visual Studio's packaged launch profile for development, or sign it before sideloading."
Write-Host "Associate the project with the Microsoft Store before creating a Store submission."
