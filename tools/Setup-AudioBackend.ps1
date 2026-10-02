[CmdletBinding()]
param(
    [string[]]$Models = @("moonshine-tiny")
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $root ".runtime"
$modelsDirectory = Join-Path $runtime "models"
New-Item -ItemType Directory -Force -Path $modelsDirectory | Out-Null
. (Join-Path $PSScriptRoot "AudioModels.ps1")
$catalog = @(Get-AudioModelCatalog)
$requested = if ($Models -contains "all") { @($catalog.id) } else { @($Models | Select-Object -Unique) }
foreach ($id in $requested) {
    if ($id -notin $catalog.id) {
        throw "Unknown model '$id'. Available models: $($catalog.id -join ', ')"
    }
}

function Get-VerifiedFile {
    param([string]$Url, [string]$Path, [string]$Sha256, [long]$Bytes)

    if ((Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-Item -LiteralPath $Path).Length -eq $Bytes -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256) {
        return
    }

    $partial = "$Path.partial"
    if ((Test-Path -LiteralPath $partial) -and (Get-Item -LiteralPath $partial).Length -ge $Bytes) {
        if ((Get-Item -LiteralPath $partial).Length -eq $Bytes -and
            (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -eq $Sha256) {
            Move-Item -LiteralPath $partial -Destination $Path -Force
            return
        }
        Remove-Item -LiteralPath $partial
    }
    Write-Host "Downloading $(Split-Path $Path -Leaf)..."
    & curl.exe --disable --location --fail --silent --show-error --retry 3 --retry-delay 2 --connect-timeout 30 `
        --speed-limit 1024 --speed-time 120 `
        --continue-at - --output $partial $Url
    if ($LASTEXITCODE -ne 0) {
        throw "Download failed for $Url. The partial file is retained for resuming."
    }
    if ((Get-Item -LiteralPath $partial).Length -ne $Bytes -or
        (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Size or checksum mismatch for $Url. The partial file was not installed."
    }
    Move-Item -LiteralPath $partial -Destination $Path -Force
}

foreach ($id in $requested) {
    $model = $catalog | Where-Object id -eq $id
    $url = "https://huggingface.co/$($model.repo)/resolve/$($model.revision)/$($model.remote_file)?download=true"
    Get-VerifiedFile -Url $url -Path (Join-Path $modelsDirectory $model.filename) -Sha256 $model.sha256 -Bytes $model.bytes
    if (-not (Test-AudioModelFile -Path (Join-Path $modelsDirectory $model.filename) -Model $model)) {
        throw "The installed model failed its size or checksum check: $id"
    }
    Write-Host "Verified $($model.display_name)."
}

Write-Host "Requested models are installed. Build the native DLL with Build-AudioNative.ps1, then reopen the app or probe."
