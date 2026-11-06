[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$executable = Join-Path $root '.runtime/record-spikes/overhead-app/OverheadSpike.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the NativeAOT overhead harness first.' }
$runStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$environmentPath = Join-Path $root ".runtime/record-spikes/overhead-environment-$runStamp.txt"
@("utc=$([DateTimeOffset]::UtcNow.ToString('O'))", "revision=$(& git -C $root rev-parse HEAD)") | Set-Content -LiteralPath $environmentPath
Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors |
    ConvertTo-Json -Compress | Add-Content -LiteralPath $environmentPath
Get-CimInstance Win32_OperatingSystem | Select-Object Version,BuildNumber,TotalVisibleMemorySize,FreePhysicalMemory,TotalVirtualMemorySize,FreeVirtualMemory |
    ConvertTo-Json -Compress | Add-Content -LiteralPath $environmentPath
$before = @{}
Get-Process | ForEach-Object { try { $before[$_.Id] = $_.CPU } catch {} }
$sampleStarted = [Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds 2
$seconds = $sampleStarted.Elapsed.TotalSeconds
Get-Process | ForEach-Object {
    try {
        if ($before.ContainsKey($_.Id)) {
            [pscustomobject]@{ Id=$_.Id; Process=$_.ProcessName; CpuCoreEquivalent=($_.CPU - $before[$_.Id]) / $seconds; WorkingSetBytes=$_.WorkingSet64 }
        }
    } catch {}
} | Sort-Object CpuCoreEquivalent -Descending | Select-Object -First 12 | ConvertTo-Json -Compress | Add-Content -LiteralPath $environmentPath
Write-Output "Environment evidence $environmentPath"
& $executable $root
if ($LASTEXITCODE -ne 0) { throw "Overhead probe failed with exit code $LASTEXITCODE." }
