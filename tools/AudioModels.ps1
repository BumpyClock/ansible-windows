function Get-AudioModelCatalog {
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot "audio-models.json") -Raw | ConvertFrom-Json
    if ($catalog.schema_version -ne 1) {
        throw "Unsupported audio model catalog version."
    }
    foreach ($model in $catalog.models) {
        if ($model.sha256 -notmatch "^[a-f0-9]{64}$" -or
            $model.revision -notmatch "^[a-f0-9]{40}$" -or
            $model.bytes -le 0 -or
            [string]::IsNullOrWhiteSpace($model.filename) -or
            $model.filename -ne [IO.Path]::GetFileName($model.filename) -or
            $model.mode -notin @("offline", "streaming")) {
            throw "Invalid model catalog entry: $($model.id)"
        }
    }
    return $catalog.models
}

function Test-AudioModelFile {
    param([string]$Path, [object]$Model)
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-Item -LiteralPath $Path).Length -eq $Model.bytes -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Model.sha256
}
