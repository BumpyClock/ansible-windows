[CmdletBinding()]
param(
    [ValidateSet("nemotron", "kroko", "both")]
    [string]$Model = "both"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "Ansible.Tests\Ansible.Tests.csproj"
$results = Join-Path $root ".runtime\validation\speaker-models"
# Tests also accept DICTATION_NATIVE_DIRECTORY, DICTATION_MODELS_DIRECTORY and DICTATION_SAMPLE_PATH.
$models = switch ($Model) {
    "nemotron" { @("nemotron-asr-0.6b-q8") }
    "kroko" { @("kroko-en-64l-q8") }
    "both" { @("nemotron-asr-0.6b-q8", "kroko-en-64l-q8") }
}
foreach ($id in $models) {
    $method = if ($id -eq "nemotron-asr-0.6b-q8") {
        "NemotronPairedCpuStreamProducesLiveSpeechAndFinalTimedSpeakers"
    } else {
        "KrokoPairedCpuStreamProducesLiveSpeechAndFinalTimedSpeakers"
    }
    $qualifiedName = "Ansible.Tests.NativeSpeakerModelTests.$method"
    $report = "$id-$([Guid]::NewGuid().ToString('N')).trx"
    & dotnet test $project --no-restore --filter "FullyQualifiedName=$qualifiedName&Category=NativeIntegration" `
        --logger "console;verbosity=normal" --logger "trx;LogFileName=$report" --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "Speaker model qualification failed for $id; not qualified." }
    $reportPath = Join-Path $results $report
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "No native test report for $id; not qualified."
    }
    [xml]$run = Get-Content -LiteralPath $reportPath -Raw
    $ns = [Xml.XmlNamespaceManager]::new($run.NameTable)
    $ns.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
    $counts = $run.SelectSingleNode("/t:TestRun/t:ResultSummary/t:Counters", $ns)
    $tests = $run.SelectNodes("/t:TestRun/t:Results/t:UnitTestResult", $ns)
    $definitions = $run.SelectNodes("/t:TestRun/t:TestDefinitions/t:UnitTest/t:TestMethod", $ns)
    if ($null -eq $counts -or $counts.total -ne "1" -or $counts.executed -ne "1" -or
        $counts.passed -ne "1" -or $tests.Count -ne 1 -or $tests[0].outcome -ne "Passed" -or
        $definitions.Count -ne 1 -or $definitions[0].className -ne "Ansible.Tests.NativeSpeakerModelTests" -or
        $definitions[0].name -ne $method -or
        $run.SelectSingleNode("/t:TestRun/t:ResultSummary", $ns).outcome -ne "Completed") {
        throw "Expected exactly one passing, executed, non-skipped native test for $id. Missing assets, skipped tests, and empty runs are not qualification. Report: $reportPath"
    }
    Write-Host "$id passed CPU paired-stream functional checks. This is not a latency or speaker-accuracy qualification."
}
