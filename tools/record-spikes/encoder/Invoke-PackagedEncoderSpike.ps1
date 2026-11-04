[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Msix,
    [switch]$Borderless,
    [ValidatePattern('^[A-Za-z0-9]{1,12}$')]
    [string]$RunId = ([Guid]::NewGuid().ToString('N').Substring(0, 12))
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$packageName = "BumpyClock.Ansible.RecordSpike.$RunId"
$layout = Join-Path $root ".runtime/record-spikes/package-layout-$RunId"
if (Get-AppxPackage -Name $packageName) {
    throw 'The isolated spike identity is already registered. Keep its evidence or unregister it explicitly before another package registration.'
}
if (Test-Path -LiteralPath $layout) { throw 'The spike layout already exists. Choose a fresh evidence directory before registering again.' }
[IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($Msix), $layout)
$publishedHelper = Join-Path $layout 'record_encoder_spike.dll'
$builtHelper = Join-Path $root '.runtime/record-spikes/encoder/record_encoder_spike.dll'
if ((Get-FileHash -LiteralPath $publishedHelper).Hash -ne (Get-FileHash -LiteralPath $builtHelper).Hash) {
    throw 'Published helper differs from the latest build. Republish before registering the spike.'
}
$evidence = @(
    "msix-sha256=$((Get-FileHash -LiteralPath $Msix).Hash)",
    "executable-sha256=$((Get-FileHash -LiteralPath (Join-Path $layout 'Ansible.exe')).Hash)",
    "helper-sha256=$((Get-FileHash -LiteralPath $publishedHelper).Hash)"
)
$evidence | Set-Content -LiteralPath (Join-Path $root ".runtime/record-spikes/package-hashes-$RunId.txt")
$manifest = Join-Path $layout 'AppxManifest.xml'
[xml]$xml = Get-Content -LiteralPath $manifest
$xml.Package.Identity.Name = $packageName
$xml.Package.Properties.DisplayName = 'Ansible Record Spike'
$xml.Save($manifest)
# Developer Mode registration creates an independent identity. It neither replaces
# the installed Ansible package nor changes certificate trust.
Add-AppxPackage -Register $manifest
$package = Get-AppxPackage -Name $packageName
if (-not $package) { throw 'Isolated package registration failed.' }
$arguments = '--record-encoder-spike'
if ($Borderless) { $arguments += ' --borderless' }
Invoke-CommandInDesktopPackage -PackageFamilyName $package.PackageFamilyName -AppId App `
    -Command (Join-Path $layout 'Ansible.exe') -Args $arguments -PreventBreakaway
$results = Join-Path $env:LOCALAPPDATA "Packages/$($package.PackageFamilyName)/LocalState/record-spikes"
Write-Output "Package $($package.PackageFullName)"
Write-Output "Results $results"
Write-Output 'The explicit five-second worker exits after finalizing and inspecting its MP4. Read result.txt after it exits.'
