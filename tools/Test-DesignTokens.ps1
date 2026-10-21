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
$rootKeySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($node in $tokenDocument.SelectNodes('/*/*[@x:Key]', $namespaceManager)) {
    [void]$rootKeySet.Add($node.GetAttribute('Key', $namespaceManager.LookupNamespace('x')))
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

$lightDarkInvariantColors = @(
    'BrandAccentColor', 'BrandOnAccentColor', 'BrandAccentStrongColor',
    'BrandAccentSoftColor', 'BrandAccentForegroundLightColor',
    'BrandAccentForegroundDarkColor', 'AppBrandVoiceDetailColor',
    'AppBrandReplyDetailColor'
)
foreach ($key in $lightDarkInvariantColors) {
    $light = $tokenDocument.SelectSingleNode("//*[local-name()='ResourceDictionary' and @x:Key='Light']/*[@x:Key='$key']", $namespaceManager)
    $dark = $tokenDocument.SelectSingleNode("//*[local-name()='ResourceDictionary' and @x:Key='Dark']/*[@x:Key='$key']", $namespaceManager)
    Assert-Condition ($null -ne $light -and $null -ne $dark -and $light.InnerText -eq $dark.InnerText) `
        "Light and Dark canonical color '$key' must remain equivalent."
}

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
    'SettingsRowRowSpacing' = 'Double'
    'SettingsRowDescriptionSpacing' = 'Double'
    'SettingsRowDescriptionMaxWidth' = 'Double'
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

$themeBrushKeys = @($tokenKeys | Where-Object { $_ -match 'Brush$' })
foreach ($themeName in @('Light', 'Dark', 'HighContrast')) {
    Assert-Condition $themeKeySets.ContainsKey($themeName) "$themeName theme dictionary is unavailable for semantic brush coverage."
}
foreach ($key in $themeBrushKeys | Sort-Object -Unique) {
    foreach ($themeName in @('Light', 'Dark', 'HighContrast')) {
        Assert-Condition ($themeKeySets.ContainsKey($themeName) -and $themeKeySets[$themeName].Contains($key)) `
            "Brush '$key' must be defined in the $themeName theme dictionary."
    }
}

# Light and Dark brushes use local canonical colors. High Contrast brush colors
# must remain ThemeResource references so Windows can update a live HC palette.
foreach ($themeName in @('Light', 'Dark')) {
    $themeNode = $tokenDocument.SelectSingleNode("//*[local-name()='ResourceDictionary' and @x:Key='$themeName']", $namespaceManager)
    foreach ($brush in $themeNode.SelectNodes('./*[local-name()="SolidColorBrush" and @x:Key]', $namespaceManager)) {
        $colorReference = $brush.GetAttribute('Color')
        if ($colorReference -match '^\{StaticResource ([^}]+)\}$') {
            Assert-Condition ($themeKeySets[$themeName].Contains($Matches[1]) -or $rootKeySet.Contains($Matches[1])) `
                "$themeName brush '$($brush.GetAttribute('Key', $namespaceManager.LookupNamespace('x')))' references missing canonical color '$($Matches[1])'."
        } elseif ($colorReference -notmatch '^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$') {
            Assert-Condition $false "$themeName brush '$($brush.GetAttribute('Key', $namespaceManager.LookupNamespace('x')))' must use StaticResource or a literal theme color."
        }
    }
}

$highContrastBrushAliases = @{
    'AppTextBrush' = 'SystemColorWindowTextColor'
    'AppSegmentSelectedBrush' = 'SystemColorHighlightColor'
    'AppSelectedTextBrush' = 'SystemColorHighlightTextColor'
    'AppSelectedIconBrush' = 'SystemColorHighlightTextColor'
}
$highContrastNode = $tokenDocument.SelectSingleNode("//*[local-name()='ResourceDictionary' and @x:Key='HighContrast']", $namespaceManager)
foreach ($alias in $highContrastBrushAliases.GetEnumerator()) {
    $brush = $highContrastNode.SelectSingleNode("./*[@x:Key='$($alias.Key)']", $namespaceManager)
    $expected = "{ThemeResource $($alias.Value)}"
    Assert-Condition ($null -ne $brush -and $brush.GetAttribute('Color') -eq $expected) `
        "HighContrast semantic brush '$($alias.Key)' must alias '$($alias.Value)'."
}
foreach ($brush in $highContrastNode.SelectNodes('./*[local-name()="SolidColorBrush" and @x:Key]', $namespaceManager)) {
    Assert-Condition ($brush.GetAttribute('Color') -match '^\{ThemeResource SystemColor[A-Za-z]+\}$') `
        "HighContrast brush '$($brush.GetAttribute('Key', $namespaceManager.LookupNamespace('x')))' must use a ThemeResource SystemColor alias."
}

$removedPaletteCopies = @(
    'TextFillColorPrimary', 'TextFillColorSecondary', 'TextFillColorTertiary', 'TextFillColorDisabled',
    'TextOnAccentFillColorPrimary', 'TextOnAccentFillColorSecondary',
    'ControlFillColorDefault', 'ControlFillColorSecondary', 'ControlFillColorTertiary',
    'ControlStrongFillColorDefault', 'ControlStrongFillColorDisabled',
    'ControlStrokeColorDefault', 'ControlStrokeColorSecondary', 'ControlStrokeColorOnAccent', 'ControlStrokeColorDisabled',
    'CardBackgroundFillColorDefault', 'CardBackgroundFillColorSecondary', 'CardBackgroundFillColorTertiary', 'CardStrokeColorDefault',
    'LayerFillColorDefault', 'LayerFillColorAlt', 'LayerFillColorDisabled',
    'SubtleFillColorTransparent', 'SubtleFillColorSecondary', 'SubtleFillColorTertiary', 'SubtleFillColorDisabled',
    'DividerStrokeColorDefault', 'DividerStrokeColorSecondary',
    'AccentFillColorDefault', 'AccentFillColorSecondary', 'AccentFillColorTertiary',
    'AccentTextFillColorPrimary', 'AccentTextFillColorTertiary', 'AccentTextFillColorDisabled'
)
foreach ($themeName in @('Light', 'Dark')) {
    foreach ($key in $removedPaletteCopies) {
        Assert-Condition (-not $themeKeySets[$themeName].Contains($key)) "$themeName keeps copied semantic color '$key'; use the canonical palette color through its brush alias."
    }
}

$appContent = Get-Content -LiteralPath $appPath -Raw
$controlsIndex = $appContent.IndexOf('<XamlControlsResources', [StringComparison]::Ordinal)
$tokensIndex = $appContent.IndexOf('<ResourceDictionary Source="DesignTokens.xaml"', [StringComparison]::Ordinal)
Assert-Condition ($controlsIndex -ge 0) 'App.xaml does not merge XamlControlsResources.'
Assert-Condition ($tokensIndex -ge 0) 'App.xaml does not merge DesignTokens.xaml.'
Assert-Condition ($controlsIndex -ge 0 -and $tokensIndex -gt $controlsIndex) 'DesignTokens.xaml must merge after XamlControlsResources.'
Assert-Condition ((Get-Content -LiteralPath $tokenPath -Raw) -notmatch '\{StaticResource SystemColor') `
    'DesignTokens.xaml must use ThemeResource for system colors so live High Contrast palette changes update brushes.'

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
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Output ("Design token checks passed. {0} semantic references, {1} token keys, themes: Light/Dark/HighContrast." -f $semanticReferences.Count, $tokenKeys.Count)
