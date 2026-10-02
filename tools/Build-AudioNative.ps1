[CmdletBinding()]
param(
    [ValidateRange(1, 8)]
    [int]$Jobs = 2
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $root ".runtime"
$tools = Join-Path $runtime "native-tools"
$source = Join-Path $runtime "audio-native-src"
$build = Join-Path $runtime "audio-native-build"
$output = Join-Path $runtime "native"
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

$installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
$vswhere = Join-Path $installer "vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "The Visual Studio MSVC x64 workload is required." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
$env:PATH = "$installer;" + ($env:PATH -replace '"', '')
$configure = "`"$cmake`" -S `"$source`" -B `"$build`" -G Ninja " +
    "-DCMAKE_MAKE_PROGRAM=`"$ninja`" -DCMAKE_BUILD_TYPE=Release " +
    "-DAUDIOCPP_BUILD_C_API=ON -DAUDIOCPP_DEPLOYMENT_BUILD=ON " +
    "-DAUDIOCPP_MODEL_SET=custom -DAUDIOCPP_MODELS=moonshine_asr,vibevoice_asr,qwen3_asr,nemotron_asr " +
    "-DENGINE_ENABLE_CUDA=OFF -DENGINE_ENABLE_HIP=OFF -DENGINE_ENABLE_VULKAN=OFF " +
    "-DENGINE_ENABLE_METAL=OFF -DENGINE_ENABLE_NATIVE_CPU=OFF -DENGINE_ENABLE_LLAMAFILE=OFF " +
    "-DGGML_AVX2=ON -DGGML_AVX512=OFF -DENGINE_BUILD_TESTS=OFF"
$compile = "`"$cmake`" --build `"$build`" --target audiocpp --parallel $Jobs"
& $env:ComSpec /d /s /c "call `"$vcvars`" >nul && $configure && $compile"
if ($LASTEXITCODE -ne 0) { throw "The audio.cpp C ABI build failed." }

Copy-Item -LiteralPath (Join-Path $build "bin\audiocpp.dll") -Destination $output -Force
$redist = Get-ChildItem -LiteralPath (Join-Path $vs "VC\Redist\MSVC") -Filter vcomp140.dll -Recurse |
    Where-Object FullName -Match "\\x64\\" | Select-Object -First 1
if ($null -eq $redist) { throw "The x64 OpenMP runtime is missing from the Visual Studio redistributables." }
Copy-Item -LiteralPath $redist.FullName -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $source "LICENSE") -Destination (Join-Path $output "audio.cpp-LICENSE") -Force
Write-Host "Direct native integration ready: $(Join-Path $output 'audiocpp.dll')"
Write-Host "No HTTP server or listener is required."
