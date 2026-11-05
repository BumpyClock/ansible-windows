[CmdletBinding()]
param([string]$RepoRoot)

# Negative fixtures must fail before PE tooling. Current fixtures must reach PE inspection.
# When real runtimes exist, also test their entire dependency closure without executing them.
$ErrorActionPreference = 'Stop'
$RepoRoot = if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    Split-Path -Parent $PSScriptRoot
} else { (Resolve-Path -LiteralPath $RepoRoot).Path }
$toolsDir = Join-Path $RepoRoot 'tools'
$buildScript = Join-Path $toolsDir 'Build-AudioNative.ps1'
$currentPatchNames = @('audio-vulkan-loader.patch', 'audio-nemotron-stream-timestamps.patch')
$currentModelFamilies = @('moonshine_asr', 'vibevoice_asr', 'qwen3_asr', 'nemotron_asr', 'nemotron_3_diar', 'kroko_asr')
$currentPatches = @($currentPatchNames | ForEach-Object {
    [ordered]@{
        file = $_
        sha256 = (Get-FileHash -LiteralPath (Join-Path $toolsDir "patches\$_") -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$fixtureRoot = Join-Path $RepoRoot ".runtime\native-recipe-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
$failures = [Collections.Generic.List[string]]::new()
$checks = 0
function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    $script:checks++
    if (-not $Condition) { $failures.Add($Message) }
}

function New-SyntheticRuntime {
    param([hashtable]$Overrides, [string]$Architecture = 'x64')
    $directory = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $dllPath = Join-Path $directory 'audiocpp.dll'
    Set-Content -LiteralPath $dllPath -Value 'not a real PE, only used for hash matching' -NoNewline
    $record = [ordered]@{
        source_commit = '795c45fbde0a7d29c93b22199728ff5caaec02e5'
        architecture = "windows-$Architecture"
        backends = @('cpu')
        vulkan_sdk = $null
        source_patches = $currentPatches
        model_families = $currentModelFamilies
        cpu_requirements = if ($Architecture -eq 'arm64') { @('ARMv8-A', 'NEON') } else {
            @('AVX2', 'FMA', 'F16C', 'BMI1', 'BMI2', 'OS-enabled AVX state')
        }
        clean_machine_qualified = $false
        closure = @(@{
            file = 'audiocpp.dll'
            sha256 = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
            imports = @()
        })
    }
    foreach ($key in $Overrides.Keys) { $record[$key] = $Overrides[$key] }
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'native-runtime.json') -Encoding utf8
    return $directory
}

function Invoke-RuntimeCheck {
    param([string]$Directory, [string]$Architecture = 'x64', [bool]$Stage = $false, [string]$Backend = 'cpu')
    try {
        & $buildScript -Architecture $Architecture -Backend $Backend -NativeDirectory $Directory `
            -VerifyRuntimeOnly:(!$Stage) -StageRuntimeOnly:$Stage *>&1 | Out-Null
        return @{ Threw = $false; Message = '' }
    } catch { return @{ Threw = $true; Message = $_.Exception.Message } }
}

try {
    $negativeCases = @(
        @{ Name = 'missing timestamp patch'; Pattern = 'patch recipe'; Overrides = @{
            source_patches = @($currentPatches[0])
        } },
        @{ Name = 'tampered patch hash'; Pattern = 'patch recipe'; Overrides = @{
            source_patches = @($currentPatches[0], @{ file = $currentPatchNames[1]; sha256 = ('0' * 64) })
        } },
        @{ Name = 'missing diarization family'; Pattern = 'model family'; Overrides = @{
            model_families = @($currentModelFamilies | Where-Object { $_ -ne 'nemotron_3_diar' })
        } },
        @{ Name = 'wrong source commit'; Pattern = 'source commit'; Overrides = @{ source_commit = ('0' * 40) } },
        @{ Name = 'missing source commit'; Pattern = 'source commit'; Overrides = @{ source_commit = $null } },
        @{ Name = 'wrong architecture'; Pattern = 'architecture'; Overrides = @{ architecture = 'windows-x86' } },
        @{ Name = 'missing architecture'; Pattern = 'architecture'; Overrides = @{ architecture = $null } },
        @{ Name = 'wrong CPU requirements'; Pattern = 'CPU requirements'; Overrides = @{ cpu_requirements = @('SVE2') } }
    )
    foreach ($architecture in @('x64', 'arm64')) {
        foreach ($case in $negativeCases) {
            $directory = New-SyntheticRuntime $case.Overrides $architecture
            foreach ($stage in @($false, $true)) {
                $result = Invoke-RuntimeCheck $directory $architecture $stage
                Assert-Condition ($result.Threw -and $result.Message -match $case.Pattern) `
                    "$architecture Stage=$stage accepted or misreported $($case.Name). Got: $($result.Message)"
            }
        }
        # Both directions of an otherwise valid x64/ARM64 record mismatch must fail.
        $other = if ($architecture -eq 'arm64') { 'x64' } else { 'arm64' }
        $directory = New-SyntheticRuntime @{} $other
        foreach ($stage in @($false, $true)) {
            $result = Invoke-RuntimeCheck $directory $architecture $stage
            Assert-Condition ($result.Threw -and $result.Message -match 'architecture') `
                "$architecture Stage=$stage accepted a $other record. Got: $($result.Message)"
        }
        $directory = New-SyntheticRuntime @{} $architecture
        foreach ($stage in @($false, $true)) {
            $result = Invoke-RuntimeCheck $directory $architecture $stage
            Assert-Condition ($result.Threw -and $result.Message -match '^(Cannot inspect PE imports|Native deployment contains a non-)') `
                "$architecture Stage=$stage current recipe did not reach PE inspection. Got: $($result.Message)"
        }
    }
    $directory = New-SyntheticRuntime @{ backends = @('cpu', 'vulkan') } 'arm64'
    $result = Invoke-RuntimeCheck $directory 'arm64'
    Assert-Condition ($result.Threw -and $result.Message -match 'CPU only') "ARM64 accepted a Vulkan record. Got: $($result.Message)"
    $result = Invoke-RuntimeCheck $directory 'arm64' $false 'vulkan'
    Assert-Condition ($result.Threw -and $result.Message -match 'Use -Architecture arm64 -Backend cpu') `
        "ARM64 Vulkan did not direct callers to CPU. Got: $($result.Message)"

    foreach ($architecture in @('x64', 'arm64')) {
        $runtimeName = if ($architecture -eq 'arm64') { 'native-arm64' } else { 'native' }
        $existing = Join-Path $RepoRoot ".runtime\$runtimeName"
        if (-not (Test-Path -LiteralPath (Join-Path $existing 'native-runtime.json'))) {
            Write-Output "Skipped real $architecture closure tests. No runtime record in $existing."
            continue
        }
        $result = Invoke-RuntimeCheck $existing $architecture
        Assert-Condition (!$result.Threw) "Existing $architecture runtime failed verification. Got: $($result.Message)"
        $directory = Join-Path $fixtureRoot "real-$architecture"
        Copy-Item -LiteralPath $existing -Destination $directory -Recurse
        $recordPath = Join-Path $directory 'native-runtime.json'
        $original = Get-Content -LiteralPath $recordPath -Raw
        $record = $original | ConvertFrom-Json
        $dependency = @($record.closure | Where-Object file -NE 'audiocpp.dll')[0]
        if (-not $dependency) { throw "Real $architecture fixture has no app-local dependency to test." }
        $dependency.sha256 = '0' * 64
        $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordPath -Encoding utf8
        foreach ($stage in @($false, $true)) {
            $result = Invoke-RuntimeCheck $directory $architecture $stage
            Assert-Condition ($result.Threw -and $result.Message -match 'dependency closure') `
                "$architecture Stage=$stage did not reject a dependency hash mismatch. Got: $($result.Message)"
        }
        $record = $original | ConvertFrom-Json
        $record.closure = @($record.closure | Where-Object file -NE $dependency.file)
        $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordPath -Encoding utf8
        foreach ($stage in @($false, $true)) {
            $result = Invoke-RuntimeCheck $directory $architecture $stage
            Assert-Condition ($result.Threw -and $result.Message -match 'dependency closure') `
                "$architecture Stage=$stage accepted a record omitting a required dependency. Got: $($result.Message)"
        }
        $record = $original | ConvertFrom-Json
        $record.closure[0].imports = @()
        $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordPath -Encoding utf8
        foreach ($stage in @($false, $true)) {
            $result = Invoke-RuntimeCheck $directory $architecture $stage
            Assert-Condition ($result.Threw -and $result.Message -match 'dependency closure') `
                "$architecture Stage=$stage accepted incorrect recorded PE imports. Got: $($result.Message)"
        }
        Set-Content -LiteralPath $recordPath -Value $original -Encoding utf8
        $dependencyPath = Join-Path $directory $dependency.file
        $bytes = [IO.File]::ReadAllBytes($dependencyPath)
        # Replace only the PE machine field. The full closure walk must reject the mixed architecture.
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
        $wrongMachine = if ($architecture -eq 'arm64') { [byte[]]@(0x64, 0x86) } else { [byte[]]@(0x64, 0xaa) }
        $bytes[$peOffset + 4] = $wrongMachine[0]
        $bytes[$peOffset + 5] = $wrongMachine[1]
        [IO.File]::WriteAllBytes($dependencyPath, $bytes)
        foreach ($stage in @($false, $true)) {
            $result = Invoke-RuntimeCheck $directory $architecture $stage
            Assert-Condition ($result.Threw -and $result.Message -match "non-$architecture PE file") `
                "$architecture Stage=$stage did not reject a dependency with another PE machine. Got: $($result.Message)"
        }
        Remove-Item -LiteralPath $dependencyPath -Force
        foreach ($stage in @($false, $true)) {
            $result = Invoke-RuntimeCheck $directory $architecture $stage
            Assert-Condition ($result.Threw -and $result.Message -match 'not app-local') `
                "$architecture Stage=$stage accepted a missing dependency. Got: $($result.Message)"
        }
    }
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $runtimeRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot '.runtime')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($runtimeRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside repository runtime: $resolvedFixture"
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}
Write-Output "Native recipe regression checks passed ($checks assertions). Source, architecture, CPU recipe, patches, models and available PE dependency closures checked."
