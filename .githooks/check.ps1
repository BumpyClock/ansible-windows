#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install .NET SDK 10.0.100 and prepare dependencies using the documented restore command.'
}
$version = & dotnet --version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($version.Trim() -ne '10.0.100') { throw 'Use .NET SDK 10.0.100 from global.json.' }
function Invoke-DotNet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$cacheLine = & dotnet nuget locals global-packages --list
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($cacheLine -notmatch '^global-packages:\s+(.+?)\s*$') { throw 'Cannot locate prepared NuGet cache.' }
$cache = $Matches[1]
if (-not (Test-Path -LiteralPath $cache -PathType Container)) {
    throw 'Prepare NuGet dependencies using the documented restore command outside the hook.'
}
# Local cache is the only restore source. Missing packages fail without network bootstrap.
Invoke-DotNet -Arguments @('restore', 'Ansible.Tests/Ansible.Tests.csproj', '--locked-mode', '--source', $cache)
Invoke-DotNet -Arguments @('format', 'whitespace', 'Ansible.Tests/Ansible.Tests.csproj', '--verify-no-changes', '--no-restore')
# Never load the app MSBuild project here: its PrepareForBuild target publishes DeviceProbe.
# Folder whitespace checks cover unlinked C# without native build or publishing side effects.
foreach ($folder in @('Ansible', 'Ansible.Probe', 'Ansible.DeviceProbe', 'Ansible.Core')) {
    Invoke-DotNet -Arguments @('format', 'whitespace', $folder, '--folder', '--verify-no-changes', '--no-restore')
}
Invoke-DotNet -Arguments @('test', 'Ansible.Tests/Ansible.Tests.csproj', '--no-restore', '--filter', 'Category!=NativeIntegration')
