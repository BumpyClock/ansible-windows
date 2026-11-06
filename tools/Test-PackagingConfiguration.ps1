[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Ansible/Ansible.csproj'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with MSBuild and the C++ workload is required.' }
$msbuild = Join-Path $vs 'MSBuild/Current/Bin/amd64/MSBuild.exe'

# Execute both production discovery blocks with a fake installer, without staging or building.
foreach ($script in @('Publish-Ansible.ps1', 'Build-AudioNative.ps1')) {
$parseErrors = $null
$publishAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot $script), [ref]$null, [ref]$parseErrors)
if ($parseErrors) { throw "Cannot parse ${script}: $parseErrors" }
$buildFunction = $publishAst.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Build-VerifiedPackage'
}, $true)
$statements = if ($buildFunction) { @($buildFunction.Body.EndBlock.Statements) } else { @($publishAst.EndBlock.Statements) }
$selectionStart = @($statements | Where-Object { $_.Extent.Text -match '^\[string\[\]\]\$vcComponents\s*=' })
$endPattern = if ($buildFunction) { '^\$vcvars\s*=' } else { '^\$toolset\s*=' }
$selectionEnd = @($statements | Where-Object { $_.Extent.Text -match $endPattern })
if ($selectionStart.Count -ne 1 -or $selectionEnd.Count -ne 1) { throw "Cannot locate the $script toolchain discovery block." }
$selection = [scriptblock]::Create($publishAst.Extent.Text.Substring(
    $selectionStart[0].Extent.StartOffset, $selectionEnd[0].Extent.StartOffset - $selectionStart[0].Extent.StartOffset))
$x64Component = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
$arm64Component = 'Microsoft.VisualStudio.Component.VC.Tools.ARM64'
$latestX64 = @{ Path = 'newer-x64-only'; Components = @($x64Component) }
$olderBoth = @{ Path = 'older-x64-and-arm64'; Components = @($x64Component, $arm64Component) }
foreach ($case in @(
    @{ Architecture = 'x64'; Instances = @($latestX64, $olderBoth); Expected = $latestX64.Path },
    @{ Architecture = 'arm64'; Instances = @($latestX64, $olderBoth); Expected = $olderBoth.Path },
    @{ Architecture = 'arm64'; Instances = @($latestX64); Expected = $null },
    @{ Architecture = 'x64'; Instances = @(); Expected = $null },
    @{ Architecture = 'x64'; MissingInstaller = $true; Expected = $null },
    @{ Architecture = 'arm64'; MissingInstaller = $true; Expected = $null }
)) {
    & {
        $Architecture = $case.Architecture
        $expectedComponents = if ($Architecture -eq 'arm64') { @($x64Component, $arm64Component) } else { @($x64Component) }
        function Test-Path { param($LiteralPath) return -not $case.MissingInstaller }
        $vswhere = {
            param([switch]$latest, [string[]]$products, [string[]]$requires, [string]$property)
            if (-not $latest -or $products.Count -ne 1 -or $products[0] -ne '*' -or $property -ne 'installationPath' -or
                @(Compare-Object $expectedComponents @($requires)).Count -ne 0) {
                throw "Unexpected vswhere arguments for $Architecture packaging."
            }
            $case.Instances | Where-Object {
                $instance = $_
                @($requires | Where-Object { $_ -notin $instance.Components }).Count -eq 0
            } | Select-Object -First 1 -ExpandProperty Path
        }
        $vs = $null
        $failure = $null
        try { . $selection } catch { $failure = $_.Exception.Message }
        if ($case.Expected) {
            if ($failure -or $vs -ne $case.Expected) { throw "Wrong $Architecture toolchain selection. Selected '$vs'; error '$failure'." }
        } else {
            if (-not $failure -or $failure -notlike "*required for $Architecture native builds and packaging*") {
                throw "Missing $Architecture toolchain was not identified: $failure"
            }
            if ($case.MissingInstaller -and $failure -notlike '*vswhere.exe is missing*') {
                throw "Missing Visual Studio Installer was not identified: $failure"
            }
            foreach ($component in $expectedComponents) {
                if (-not $failure.Contains($component)) { throw "Missing-tools error omitted '$component': $failure" }
            }
        }
    }
}
}

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
$bundleRequired = $false
try {
    & (Join-Path $PSScriptRoot 'Publish-Ansible.ps1') -Architecture x64,arm64
} catch {
    if ($_.Exception.Message -ne 'Pass -Bundle when selecting multiple architectures.') { throw }
    $bundleRequired = $true
}
if (-not $bundleRequired) { throw 'Multiple architectures were accepted without -Bundle.' }
Write-Host 'Packaging configuration passed for Debug/Release x64 and ARM64; architecture-specific MSVC selection and missing-component errors passed; x86 and mixed architectures are rejected; multiple architectures require a bundle.'
