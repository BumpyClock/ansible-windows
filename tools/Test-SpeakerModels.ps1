[CmdletBinding()]
param(
    [ValidateSet("nemotron", "kroko", "both")]
    [string]$Model = "both"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "Ansible.Tests\Ansible.Tests.csproj"
$results = Join-Path $root ".runtime\validation\speaker-models"
$models = switch ($Model) {
    "nemotron" { @("nemotron-asr-0.6b-q8") }
    "kroko" { @("kroko-en-64l-q8") }
    "both" { @("nemotron-asr-0.6b-q8", "kroko-en-64l-q8") }
}
foreach ($id in $models) {
    $report = "$id-$([Guid]::NewGuid().ToString('N')).trx"
    & dotnet test $project --no-restore --filter "FullyQualifiedName~NativeSpeakerModelTests&DisplayName~$id" `
        --logger "console;verbosity=normal" --logger "trx;LogFileName=$report" --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "Speaker model qualification failed for $id." }
    [xml]$run = Get-Content -LiteralPath (Join-Path $results $report) -Raw
    $counts = $run.TestRun.ResultSummary.Counters
    if ($counts.total -ne "1" -or $counts.passed -ne "1") {
        throw "Expected one passing native qualification for $id. A missing or skipped test is not qualification."
    }
}
