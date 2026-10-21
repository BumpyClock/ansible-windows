[CmdletBinding()]
param(
    [string]$RepoRoot
)

# Regression test for the native runtime recipe staleness check in Build-AudioNative.ps1.
# A record that is internally self-consistent (its DLL hash matches its own closure entry) can
# still describe a build compiled with an outdated patch set or model family list. This script
# proves such stale records are rejected by -VerifyRuntimeOnly/-StageRuntimeOnly, and that a
# record reflecting the current recipe clears that check.

$ErrorActionPreference = 'Stop'
$RepoRoot = if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    Split-Path -Parent $PSScriptRoot
} else {
    (Resolve-Path -LiteralPath $RepoRoot).Path
}
$toolsDir = Join-Path $RepoRoot 'tools'
$buildScript = Join-Path $toolsDir 'Build-AudioNative.ps1'
if (-not (Test-Path -LiteralPath $buildScript)) { throw "Build-AudioNative.ps1 not found: $buildScript" }

$currentPatchNames = @("audio-vulkan-loader.patch", "audio-nemotron-stream-timestamps.patch")
$currentModelFamilies = @("moonshine_asr", "vibevoice_asr", "qwen3_asr", "nemotron_asr", "nemotron_3_diar", "kroko_asr")
$currentPatches = @($currentPatchNames | ForEach-Object {
    [ordered]@{
        file = $_
        sha256 = (Get-FileHash -LiteralPath (Join-Path $toolsDir "patches\$_") -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { [void]$failures.Add($Message) }
}

function New-SyntheticRuntime {
    param([hashtable]$Overrides)
    $directory = Join-Path ([IO.Path]::GetTempPath()) ("native-recipe-test-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    # The closure walk and PE inspection only run after the recipe check; a placeholder is enough
    # here because a stale/mismatched recipe must be rejected before any PE tooling is touched.
    $dllPath = Join-Path $directory "audiocpp.dll"
    Set-Content -LiteralPath $dllPath -Value "not a real PE, only used for hash matching" -NoNewline
    $dllHash = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $record = [ordered]@{
        source_commit = "795c45fbde0a7d29c93b22199728ff5caaec02e5"
        architecture = "windows-x64"
        backends = @("cpu", "vulkan")
        vulkan_sdk = "1.3.296.0"
        source_patches = $currentPatches
        model_families = $currentModelFamilies
        cpu_requirements = @("AVX2", "FMA", "F16C", "BMI1", "BMI2", "OS-enabled AVX state")
        windows_prerequisite = "test fixture"
        redist_version = "0.0.0"
        clean_machine_qualified = $false
        closure = @(@{ file = "audiocpp.dll"; sha256 = $dllHash; imports = @() })
    }
    foreach ($key in $Overrides.Keys) { $record[$key] = $Overrides[$key] }
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory "native-runtime.json") -Encoding utf8
    return $directory
}

function Invoke-VerifyRuntimeOnly {
    param([string]$Directory)
    try {
        & $buildScript -VerifyRuntimeOnly -Backend vulkan -NativeDirectory $Directory *>&1 | Out-Null
        return @{ Threw = $false; Message = $null }
    } catch {
        return @{ Threw = $true; Message = $_.Exception.Message }
    }
}

$fixtures = @()
try {
    # Negative: a record naming only the pre-change loader patch (the reported bug scenario) with a
    # self-consistent, matching DLL hash must still be rejected.
    $staleBackendsOnly = New-SyntheticRuntime @{
        source_patches = @(@{ file = "audio-vulkan-loader.patch"; sha256 = $currentPatches[0].sha256 })
    }
    $fixtures += $staleBackendsOnly
    $result = Invoke-VerifyRuntimeOnly $staleBackendsOnly
    Assert-Condition $result.Threw "A record missing the diarization/timestamp patch was accepted instead of rejected."
    Assert-Condition ($result.Threw -and $result.Message -match 'patch recipe') `
        "Rejection of a stale patch set did not report a patch-recipe mismatch. Got: $($result.Message)"

    # Negative: a record with a patch whose content changed (hash mismatch) must be rejected even
    # when the file name set is otherwise correct.
    $tamperedPatchHash = New-SyntheticRuntime @{
        source_patches = @(
            @{ file = "audio-vulkan-loader.patch"; sha256 = $currentPatches[0].sha256 },
            @{ file = "audio-nemotron-stream-timestamps.patch"; sha256 = ("0" * 64) }
        )
    }
    $fixtures += $tamperedPatchHash
    $result = Invoke-VerifyRuntimeOnly $tamperedPatchHash
    Assert-Condition $result.Threw "A record with a tampered/mismatched patch hash was accepted instead of rejected."
    Assert-Condition ($result.Threw -and $result.Message -match 'patch recipe') `
        "Rejection of a tampered patch hash did not report a patch-recipe mismatch. Got: $($result.Message)"

    # Negative: current patches but a stale/missing compiled model family set must be rejected.
    $staleFamilies = New-SyntheticRuntime @{ model_families = @("moonshine_asr", "vibevoice_asr", "qwen3_asr", "nemotron_asr", "kroko_asr") }
    $fixtures += $staleFamilies
    $result = Invoke-VerifyRuntimeOnly $staleFamilies
    Assert-Condition $result.Threw "A record missing the diarization model family was accepted instead of rejected."
    Assert-Condition ($result.Threw -and $result.Message -match 'model family') `
        "Rejection of a stale model family set did not report a model-family mismatch. Got: $($result.Message)"

    # Positive: a record reflecting the current patch set and model family list must clear the
    # recipe check. It still fails later (the placeholder DLL is not a real PE image for dumpbin),
    # which confirms the failure moved past recipe validation rather than recipe validation
    # vacuously accepting everything.
    $current = New-SyntheticRuntime @{}
    $fixtures += $current
    $result = Invoke-VerifyRuntimeOnly $current
    Assert-Condition $result.Threw "Expected the placeholder (non-PE) DLL to fail PE inspection after passing the recipe check."
    Assert-Condition ($result.Threw -and $result.Message -notmatch 'patch recipe' -and $result.Message -notmatch 'model family') `
        "A current, correctly-recipe'd record was rejected by the recipe check itself. Got: $($result.Message)"
} finally {
    foreach ($fixture in $fixtures) {
        if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Output "Native build recipe staleness checks passed: stale patch set, tampered patch hash, and stale model family set are all rejected; a current recipe clears validation."
