[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
$RepoRoot = if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    Split-Path -Parent $PSScriptRoot
} else {
    (Resolve-Path -LiteralPath $RepoRoot).Path
}
$tokenPath = Join-Path $RepoRoot 'Ansible\DesignTokens.xaml'
$appPath = Join-Path $RepoRoot 'Ansible\App.xaml'

if (-not (Test-Path -LiteralPath $tokenPath)) {
    throw "Design token dictionary not found: $tokenPath"
}
if (-not (Test-Path -LiteralPath $appPath)) {
    throw "App.xaml not found: $appPath"
}

[xml]$tokenDocument = Get-Content -LiteralPath $tokenPath -Raw
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($tokenDocument.NameTable)
$namespaceManager.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')

$tokenKeys = @(
    $tokenDocument.SelectNodes('//*[@x:Key]', $namespaceManager) |
        ForEach-Object { $_.GetAttribute('Key', $namespaceManager.LookupNamespace('x')) }
)
$tokenKeySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($key in $tokenKeys) {
    [void]$tokenKeySet.Add($key)
}

$themeDictionaries = @(
    $tokenDocument.SelectNodes('//*[local-name()="ResourceDictionary" and @x:Key]', $namespaceManager)
)
$themeKeySets = @{}
foreach ($themeDictionary in $themeDictionaries) {
    $themeName = $themeDictionary.GetAttribute('Key', $namespaceManager.LookupNamespace('x'))
    $themeKeySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $themeDictionary.SelectNodes('./*[@x:Key]', $namespaceManager)) {
        [void]$themeKeySet.Add($node.GetAttribute('Key', $namespaceManager.LookupNamespace('x')))
    }
    $themeKeySets[$themeName] = $themeKeySet
}

$failures = [System.Collections.Generic.List[string]]::new()
function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        [void]$failures.Add($Message)
    }
}

Assert-Condition ($themeKeySets.ContainsKey('Light')) 'Light theme dictionary is missing.'
Assert-Condition ($themeKeySets.ContainsKey('Dark')) 'Dark theme dictionary is missing.'
Assert-Condition ($themeKeySets.ContainsKey('HighContrast')) 'HighContrast theme dictionary is missing.'

$typedDimensions = @{
    'CardCornerRadius' = 'CornerRadius'
    'PanelCornerRadius' = 'CornerRadius'
    'ControlCornerRadius' = 'CornerRadius'
    'CardBorderThickness' = 'Thickness'
    'SettingsCardPadding' = 'Thickness'
    'SettingsSegmentPadding' = 'Thickness'
    'SettingsSegmentCornerRadius' = 'CornerRadius'
    'SettingsSegmentTrackPadding' = 'Thickness'
    'SettingsSegmentTrackCornerRadius' = 'CornerRadius'
}
foreach ($entry in $typedDimensions.GetEnumerator()) {
    $node = $tokenDocument.SelectSingleNode("//*[@x:Key='$($entry.Key)']", $namespaceManager)
    Assert-Condition ($null -ne $node -and $node.LocalName -eq $entry.Value) "$($entry.Key) must use the $($entry.Value) XAML resource type."
}

$xamlFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'Ansible') -Recurse -Filter '*.xaml' -File |
    Where-Object { $_.FullName -ne $tokenPath -and $_.FullName -notmatch '\\(?:bin|obj)\\' })
$localStyleKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($key in $tokenKeys) { [void]$localStyleKeys.Add($key) }
foreach ($file in $xamlFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if ($null -eq $content) { $content = '' }
    foreach ($match in [regex]::Matches($content, '<Style\s+[^>]*x:Key="([A-Za-z_][A-Za-z0-9_]*)"')) {
        [void]$localStyleKeys.Add($match.Groups[1].Value)
    }
}
$platformStyleKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($key in @('AccentButtonStyle', 'SubtleButtonStyle')) { [void]$platformStyleKeys.Add($key) }
$styleReferencePattern = '(?:Style|BasedOn)\s*=\s*"\{StaticResource\s+([A-Za-z_][A-Za-z0-9_]*)\}"'
$semanticReferences = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $xamlFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if ($null -eq $content) { $content = '' }
    foreach ($match in [regex]::Matches($content, $styleReferencePattern)) {
        $styleKey = $match.Groups[1].Value
        Assert-Condition ($localStyleKeys.Contains($styleKey) -or $platformStyleKeys.Contains($styleKey)) "$($file.FullName): style resource '$styleKey' is not defined locally or by WinUI."
    }
    foreach ($match in [regex]::Matches($content, '(?:Theme|Static)Resource\s+([A-Za-z_][A-Za-z0-9_]*)')) {
        $key = $match.Groups[1].Value
        if ($key -match '^(?:App|Pill|Brand)') {
            [void]$semanticReferences.Add($key)
            Assert-Condition $tokenKeySet.Contains($key) "$($file.FullName): semantic resource '$key' is not defined in DesignTokens.xaml."
        }
    }
}

$semanticBrushKeys = @($tokenKeys | Where-Object { $_ -match '^(?:App|Pill|Brand).+Brush$' })
foreach ($themeName in @('Light', 'Dark', 'HighContrast')) {
    foreach ($key in $semanticBrushKeys) {
        Assert-Condition $themeKeySets[$themeName].Contains($key) "$themeName theme is missing semantic brush '$key'."
    }
}

$appContent = Get-Content -LiteralPath $appPath -Raw
$controlsIndex = $appContent.IndexOf('<XamlControlsResources', [StringComparison]::Ordinal)
$tokensIndex = $appContent.IndexOf('<ResourceDictionary Source="DesignTokens.xaml"', [StringComparison]::Ordinal)
Assert-Condition ($controlsIndex -ge 0) 'App.xaml does not merge XamlControlsResources.'
Assert-Condition ($tokensIndex -ge 0) 'App.xaml does not merge DesignTokens.xaml.'
Assert-Condition ($controlsIndex -ge 0 -and $tokensIndex -gt $controlsIndex) 'DesignTokens.xaml must merge after XamlControlsResources.'

$hexPattern = '#[0-9A-Fa-f]{6,8}'
foreach ($file in $xamlFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if ($null -eq $content) { $content = '' }
    foreach ($match in [regex]::Matches($content, $hexPattern)) {
        if ($match.Value -notmatch '^#00(?:[0-9A-Fa-f]{6})?$') {
            [void]$failures.Add("$($file.FullName): raw color '$($match.Value)' is outside DesignTokens.xaml.")
        }
    }
}

$csharpFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'Ansible') -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '\\(?:bin|obj)\\' })
foreach ($file in $csharpFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    if ($null -eq $content) { $content = '' }
    foreach ($match in [regex]::Matches($content, 'FromArgb\s*\(([^)]*)\)')) {
        $parts = @($match.Groups[1].Value.Split(',') | ForEach-Object { $_.Trim() })
        if ($parts.Count -ge 4 -and $parts[0] -notmatch '^(?:0|0x0{1,2}|0x00)$') {
            [void]$failures.Add("$($file.FullName): Color.FromArgb palette constant '$($match.Value)' is outside DesignTokens.xaml.")
        }
    }
}

$interAssets = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'Ansible') -Recurse -File |
    Where-Object { $_.Name -match '(?i)inter' -and $_.FullName -notmatch '\\(?:bin|obj)\\' })
Assert-Condition ($interAssets.Count -eq 0) ('Inter font/assets remain under Ansible: ' + (($interAssets | ForEach-Object FullName) -join ', '))
Assert-Condition ((Get-Content -LiteralPath $tokenPath -Raw) -notmatch '(?i)(?<![A-Za-z])Inter(?![A-Za-z])') 'DesignTokens.xaml still references Inter; use the existing WinUI font.'

foreach ($page in Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'Ansible') -Filter '*Page.xaml' -File) {
    [xml]$pageDocument = Get-Content -LiteralPath $page.FullName -Raw
    $pageContent = @($pageDocument.DocumentElement.ChildNodes | Where-Object {
        $_.NodeType -eq 'Element' -and $_.LocalName -notlike '*.*'
    })
    Assert-Condition ($pageContent.Count -eq 1 -and $pageContent[0].LocalName -eq 'PageCanvas') `
        ("{0}: pages must render into the shared PageCanvas; it owns width, padding, and viewport bounds." -f $page.Name)
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output ("Design token checks passed. {0} semantic references, {1} token keys, themes: Light/Dark/HighContrast." -f $semanticReferences.Count, $tokenKeys.Count)
