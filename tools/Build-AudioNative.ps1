[CmdletBinding()]
param(
    [ValidateSet("cpu", "vulkan")]
    [string]$Backend = "vulkan",
    [string]$VulkanSdk = "",
    [ValidateRange(1, 8)]
    [int]$Jobs = 2,
    [switch]$StageRuntimeOnly,
    [switch]$VerifyRuntimeOnly,
    [string]$NativeDirectory = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $root ".runtime"
$tools = Join-Path $runtime "native-tools"
$source = Join-Path $runtime "audio-native-src"
$build = Join-Path $runtime "audio-native-build-$Backend"
$output = Join-Path $runtime "native"
if ($NativeDirectory) { $output = [IO.Path]::GetFullPath($NativeDirectory) }
[string[]]$runtimeBackends = @("cpu")
if ($Backend -eq "vulkan") { $runtimeBackends += "vulkan" }
$sdkVersion = $null
[object[]]$sourcePatches = @()
if ($StageRuntimeOnly -or $VerifyRuntimeOnly) {
    $recordPath = Join-Path $output "native-runtime.json"
    if (-not (Test-Path -LiteralPath $recordPath)) { throw "Build the native runtime before staging or verifying it." }
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    $runtimeBackends = if ($record.backends) { @($record.backends) } else { @("cpu") }
    $sdkVersion = $record.vulkan_sdk
    $sourcePatches = if ($record.source_patches) { @($record.source_patches) } else { @() }
}
if (-not [Environment]::Is64BitProcess -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw "The qualified native build and deployment path requires Windows x64."
}

$installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
$vswhere = Join-Path $installer "vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "The Visual Studio MSVC x64 workload is required." }
$toolset = Get-ChildItem -LiteralPath (Join-Path $vs "VC\Tools\MSVC") -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$dumpbin = Join-Path $toolset.FullName "bin\Hostx64\x64\dumpbin.exe"
$redistRoot = Get-ChildItem -LiteralPath (Join-Path $vs "VC\Redist\MSVC") -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+$' |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $redistRoot) { throw "The installed Visual Studio x64 redistributables are missing." }
$redistDirectories = @(
    (Join-Path $redistRoot.FullName "x64\Microsoft.VC145.CRT"),
    (Join-Path $redistRoot.FullName "x64\Microsoft.VC145.OpenMP"),
    (Join-Path $redistRoot.FullName "x64\Microsoft.VC143.CRT"),
    (Join-Path $redistRoot.FullName "x64\Microsoft.VC143.OpenMP")
) | Where-Object { Test-Path -LiteralPath $_ }
$systemImports = @(
    "kernel32.dll", "advapi32.dll", "ntdll.dll", "user32.dll", "gdi32.dll",
    "ole32.dll", "oleaut32.dll", "combase.dll", "rpcrt4.dll", "shell32.dll",
    "shlwapi.dll", "ws2_32.dll", "bcrypt.dll", "crypt32.dll", "secur32.dll",
    "version.dll", "winmm.dll", "ucrtbase.dll"
)

function Get-PeImports {
    param([string]$Path)
    $details = & $dumpbin /nologo /headers /imports $Path
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect PE imports for $Path." }
    if (-not ($details -match '8664 machine')) { throw "Native deployment contains a non-x64 PE file: $Path." }
    return @($details | ForEach-Object {
        if ($_ -match '^\s+([a-zA-Z0-9_.-]+\.dll)\s*$') { $Matches[1].ToLowerInvariant() }
    } | Sort-Object -Unique)
}

function Test-NativeClosure {
    param([bool]$Stage)
    $native = Join-Path $output "audiocpp.dll"
    if (-not (Test-Path -LiteralPath $native)) { throw "Build the native backend before staging or publishing." }
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($native)
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $closure = @()
    while ($queue.Count -gt 0) {
        $path = $queue.Dequeue()
        if (-not $visited.Add($path)) { continue }
        $imports = @(Get-PeImports $path)
        if ("vulkan-1.dll" -in $imports) {
            throw "Vulkan must use the optional system loader so CPU recognition does not require a GPU driver. Rebuild the patched native runtime."
        }
        $closure += [ordered]@{
            file = [IO.Path]::GetFileName($path)
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            imports = $imports
        }
        foreach ($name in $imports) {
            if ($name -match '^(api-ms-win-|ext-ms-win-)' -or $name -in $systemImports) { continue }
            $local = Join-Path $output $name
            if ($Stage) {
                $redist = $redistDirectories | ForEach-Object { Join-Path $_ $name } |
                    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
                if ($redist) { Copy-Item -LiteralPath $redist -Destination $local -Force }
            }
            if (-not (Test-Path -LiteralPath $local)) {
                throw "Native import '$name' required by '$path' is not app-local or a qualified Windows 10 system dependency."
            }
            $queue.Enqueue($local)
        }
    }
    foreach ($required in @("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $required))) {
            throw "The app-local runtime is incomplete: $required is missing."
        }
    }
    $notices = Join-Path $output "native-notices"
    if ($Stage) {
        New-Item -ItemType Directory -Force -Path $notices | Out-Null
        Copy-Item -LiteralPath (Join-Path $vs "Licenses\1033\Redist.txt") `
            -Destination (Join-Path $notices "VisualStudio-Redist.txt") -Force
        Copy-Item -LiteralPath (Join-Path $vs "Licenses\1033\ThirdPartyNotices.txt") `
            -Destination (Join-Path $notices "VisualStudio-ThirdPartyNotices.txt") -Force
        Get-ChildItem -LiteralPath $source -File -Recurse |
            Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)([.-].*)?$' -and $_.FullName -notmatch '\\.git\\' } |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($source, $_.FullName)
                $target = Join-Path (Join-Path $notices "audio.cpp") $relative
                New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target -Force
            }
        Copy-Item -LiteralPath (Join-Path $source "LICENSE") -Destination (Join-Path $output "audio.cpp-LICENSE") -Force
        $sample = Join-Path $source "assets\resources\sample_16k.wav"
        if (-not (Test-Path -LiteralPath $sample -PathType Leaf)) {
            throw "The pinned audio.cpp source is missing its public validation sample."
        }
        $validation = Join-Path $runtime "validation"
        New-Item -ItemType Directory -Force -Path $validation | Out-Null
        Copy-Item -LiteralPath $sample -Destination (Join-Path $validation "sample_16k.wav") -Force
        [ordered]@{
            source_commit = "795c45fbde0a7d29c93b22199728ff5caaec02e5"
            architecture = "windows-x64"
            backends = $runtimeBackends
            vulkan_sdk = $sdkVersion
            source_patches = $sourcePatches
            cpu_requirements = @("AVX2", "FMA", "F16C", "BMI1", "BMI2", "OS-enabled AVX state")
            windows_prerequisite = "Windows 10 or newer supplies UCRT and Windows API-set imports."
            redist_version = $redistRoot.Name
            clean_machine_qualified = $false
            closure = $closure
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output "native-runtime.json") -Encoding utf8
    }
    foreach ($notice in @("VisualStudio-Redist.txt", "VisualStudio-ThirdPartyNotices.txt", "audio.cpp\LICENSE")) {
        if (-not (Test-Path -LiteralPath (Join-Path $notices $notice))) { throw "Native deployment notice '$notice' is missing." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $output "native-runtime.json"))) { throw "The native deployment record is missing." }
    Write-Host "Verified x64 native PE import closure ($($visited.Count) app-local files). Windows 10+ system imports remain prerequisites."
    Write-Host "CPU requires AVX2/FMA/F16C/BMI1/BMI2. Clean-machine qualification has not been performed."
    Write-Host "Compiled backends: $($runtimeBackends -join ', '). Vulkan recognition requires a compatible GPU driver; CPU remains available without it."
}

if ($VerifyRuntimeOnly) { Test-NativeClosure $false; return }
if ($StageRuntimeOnly) {
    $commit = & git -C $source rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -ne "795c45fbde0a7d29c93b22199728ff5caaec02e5") {
        throw "The native source is not the expected audio.cpp v0.9.0 commit."
    }
    Test-NativeClosure $true
    return
}
New-Item -ItemType Directory -Force -Path $tools, $output | Out-Null

function Get-NativeTool {
    param([string]$Name, [string]$Url, [string]$Sha256)
    $archive = Join-Path $tools "$Name.zip"
    if (-not (Test-Path -LiteralPath $archive) -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Sha256) {
        & curl.exe --disable --location --fail --retry 3 --output "$archive.partial" $Url
        if ($LASTEXITCODE -ne 0) { throw "Cannot download $Name." }
        if ((Get-FileHash -LiteralPath "$archive.partial" -Algorithm SHA256).Hash -ne $Sha256) {
            throw "Checksum mismatch for $Name."
        }
        Move-Item -LiteralPath "$archive.partial" -Destination $archive -Force
    }
    $destination = Join-Path $tools $Name
    if (-not (Test-Path -LiteralPath $destination)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $destination
    }
    return $destination
}

$cmakeRoot = Get-NativeTool "cmake-3.31.8" `
    "https://github.com/Kitware/CMake/releases/download/v3.31.8/cmake-3.31.8-windows-x86_64.zip" `
    "81aa9964dbabd71fe02e7ec50472fd3ad56138c49944515ece9001efbff8d719"
$ninjaRoot = Get-NativeTool "ninja-1.13.2" `
    "https://github.com/ninja-build/ninja/releases/download/v1.13.2/ninja-win.zip" `
    "07fc8261b42b20e71d1720b39068c2e14ffcee6396b76fb7a795fb460b78dc65"
$cmake = Join-Path $cmakeRoot "cmake-3.31.8-windows-x86_64\bin\cmake.exe"
$ninja = Join-Path $ninjaRoot "ninja.exe"

if (-not (Test-Path -LiteralPath $source)) {
    & git clone --quiet --depth 1 --branch v0.9.0 https://github.com/0xShug0/audio.cpp.git $source
    if ($LASTEXITCODE -ne 0) { throw "Cannot clone the pinned audio.cpp source." }
}
$commit = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -ne "795c45fbde0a7d29c93b22199728ff5caaec02e5") {
    throw "The native source is not the expected audio.cpp v0.9.0 commit."
}

$patch = Join-Path $PSScriptRoot "patches\audio-vulkan-loader.patch"
$patchCheck = & git -C $source apply --check $patch 2>&1
if ($LASTEXITCODE -eq 0) {
    & git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw "Cannot apply the optional Vulkan loader patch." }
} else {
    $reverseCheck = & git -C $source apply --reverse --check $patch 2>&1
    if ($LASTEXITCODE -ne 0) { throw "The pinned source does not match the Vulkan loader patch: $patchCheck" }
}
$sourcePatches = @(@{
    file = "audio-vulkan-loader.patch"
    sha256 = (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant()
})

$vulkanFlags = "-DENGINE_ENABLE_VULKAN=OFF"
if ($Backend -eq "vulkan") {
    if (-not $VulkanSdk) { $VulkanSdk = $env:VULKAN_SDK }
    if (-not $VulkanSdk) { $VulkanSdk = [Environment]::GetEnvironmentVariable("VULKAN_SDK", "Machine") }
    if (-not $VulkanSdk) { $VulkanSdk = [Environment]::GetEnvironmentVariable("VULKAN_SDK", "User") }
    if (-not $VulkanSdk -or -not (Test-Path -LiteralPath (Join-Path $VulkanSdk "Bin\glslc.exe"))) {
        throw "The Vulkan SDK shader compiler is missing. Install KhronosGroup.VulkanSDK with winget, then pass -VulkanSdk <SDK directory> or set VULKAN_SDK."
    }
    $VulkanSdk = [IO.Path]::GetFullPath($VulkanSdk)
    $env:VULKAN_SDK = $VulkanSdk
    $sdkVersion = Split-Path $VulkanSdk -Leaf
    $vulkanFlags = "-DENGINE_ENABLE_VULKAN=ON"
}

$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
$env:PATH = "$installer;$ninjaRoot;" + ($env:PATH -replace '"', '')
$configure = "`"$cmake`" -S `"$source`" -B `"$build`" -G Ninja " +
    "-DCMAKE_MAKE_PROGRAM=`"$ninja`" -DCMAKE_BUILD_TYPE=Release " +
    "-DAUDIOCPP_BUILD_C_API=ON -DAUDIOCPP_DEPLOYMENT_BUILD=ON " +
    "-DAUDIOCPP_MODEL_SET=custom -DAUDIOCPP_MODELS=moonshine_asr,vibevoice_asr,qwen3_asr,nemotron_asr " +
    "-DENGINE_ENABLE_CUDA=OFF -DENGINE_ENABLE_HIP=OFF $vulkanFlags " +
    "-DENGINE_ENABLE_METAL=OFF -DENGINE_ENABLE_NATIVE_CPU=OFF -DENGINE_ENABLE_LLAMAFILE=OFF " +
    "-DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_BMI2=ON -DGGML_AVX512=OFF -DENGINE_BUILD_TESTS=OFF"
$compile = "`"$cmake`" --build `"$build`" --target audiocpp --parallel $Jobs"
& $env:ComSpec /d /s /c "call `"$vcvars`" >nul && $configure && $compile"
if ($LASTEXITCODE -ne 0) { throw "The audio.cpp C ABI build failed." }

Copy-Item -LiteralPath (Join-Path $build "bin\audiocpp.dll") -Destination $output -Force
Test-NativeClosure $true
Write-Host "Direct native integration ready: $(Join-Path $output 'audiocpp.dll')"
Write-Host "No HTTP server or listener is required."
