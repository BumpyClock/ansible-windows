#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
if (-not (Get-Command sh -ErrorAction SilentlyContinue)) {
    throw 'Add Git Bash sh to PATH, then rerun this opt-in installer.'
}
& sh (Join-Path $PSScriptRoot '../scripts/install-hooks.sh')
exit $LASTEXITCODE
