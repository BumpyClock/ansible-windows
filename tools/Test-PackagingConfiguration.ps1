[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Ansible/Ansible.csproj'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with MSBuild and the C++ workload is required.' }
$msbuild = Join-Path $vs 'MSBuild/Current/Bin/amd64/MSBuild.exe'

foreach ($configuration in @('Debug', 'Release')) {
    foreach ($platform in @('x64', 'ARM64')) {
        $architecture = $platform.ToLowerInvariant()
        $result = & $msbuild $project -nologo "-p:Platform=$platform" "-p:Configuration=$configuration" `
            '-getProperty:PlatformTarget,RuntimeIdentifier,NativeRuntimeDirectory,RecordCaptureDirectory,EncoderSpikeDirectory,PublishProfile,AppxBundle,AppxBundlePlatforms'
        if ($LASTEXITCODE -ne 0) { throw "Cannot evaluate $configuration/$platform." }
        $properties = ($result | Out-String | ConvertFrom-Json).Properties
        $suffix = if ($architecture -eq 'arm64') { '-arm64' } else { '' }
        $expected = @{
            PlatformTarget = $platform
            RuntimeIdentifier = "win-$architecture"
            NativeRuntimeDirectory = "..\.runtime\native$suffix"
            RecordCaptureDirectory = "..\.runtime\record-capture$suffix"
            EncoderSpikeDirectory = "..\.runtime\record-spikes\encoder$suffix"
            PublishProfile = "win-$architecture"
            AppxBundle = 'Always'
            AppxBundlePlatforms = $platform
        }
        foreach ($name in $expected.Keys) {
            if ($properties.$name -cne $expected[$name]) {
                throw "$configuration/$platform $name was '$($properties.$name)', expected '$($expected[$name])'."
            }
        }
    }
}

foreach ($arguments in @(
    @('-p:Platform=x86'),
    @('-p:Platform=ARM64', '-p:RuntimeIdentifier=win-x64'),
    @('-p:Platform=x64', '-p:PlatformTarget=ARM64')
)) {
    $result = & $msbuild $project -nologo -t:RequireQualifiedDeployment @arguments 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $result -notmatch 'Use matching x64/win-x64 or ARM64/win-arm64') {
        throw "Invalid deployment was not rejected by the architecture gate ($arguments): $result"
    }
}
Write-Host 'Packaging configuration passed for Debug/Release x64 and ARM64; x86 and mixed architectures are rejected.'
