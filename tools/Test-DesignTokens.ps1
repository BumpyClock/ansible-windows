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
    'SystemAccentColor', 'BrandAccentColor', 'BrandOnAccentColor', 'BrandAccentStrongColor', 'BrandAccentSoftColor'
)
foreach ($key in $lightDarkInvariantColors) {
    $light = $tokenDocument.SelectSingleNode("//*[local-name()='ResourceDictionary' and @x:Key='Light']/*[@x:Key='$key']", $namespaceManager)
    $dark = $tokenDocument.SelectSingleNode("//*[local-name()='ResourceDictionary' and @x:Key='Dark']/*[@x:Key='$key']", $namespaceManager)
    Assert-Condition ($null -ne $light -and $null -ne $dark -and $light.InnerText -eq $dark.InnerText) `
        "Light and Dark canonical color '$key' must remain equivalent."
}

$typedDimensions = @{
    'CardBorderThickness' = 'Thickness'
    'SettingsCardPadding' = 'Thickness'
    'SettingsRowRowSpacing' = 'Double'
    'SettingsRowDescriptionSpacing' = 'Double'
    'SettingsRowDescriptionMaxWidth' = 'Double'
    'SectionBadgePadding' = 'Thickness'
    'SectionBadgeMinHeight' = 'Double'
    'SectionBadgeMaxWidth' = 'Double'
}
foreach ($entry in $typedDimensions.GetEnumerator()) {
    $node = $tokenDocument.SelectSingleNode("//*[@x:Key='$($entry.Key)']", $namespaceManager)
    Assert-Condition ($null -ne $node -and $node.LocalName -eq $entry.Value) "$($entry.Key) must use the $($entry.Value) XAML resource type."
}

$badgeHeight = $tokenDocument.SelectSingleNode("//*[@x:Key='SectionBadgeMinHeight']", $namespaceManager)
Assert-Condition ([double]$badgeHeight.InnerText -ge 24) 'Section badges need at least 24 effective pixels of height for caption padding.'
foreach ($view in @('HomeHistoryView.xaml', 'HomeInsightsView.xaml')) {
    [xml]$viewDocument = Get-Content -LiteralPath (Join-Path $RepoRoot "Ansible\$view") -Raw
    $badge = $viewDocument.SelectSingleNode("//*[local-name()='Badge' and (@x:Name='HistoryCountPill' or @x:Name='HomeStreakPill')]", $namespaceManager)
    Assert-Condition ($null -ne $badge) "$view must use the shared Badge component."
}
[xml]$badgeDocument = Get-Content -LiteralPath (Join-Path $RepoRoot 'Ansible\Badge.xaml') -Raw
$badgeRoot = $badgeDocument.DocumentElement
Assert-Condition ($badgeRoot.GetAttribute('HorizontalAlignment') -eq 'Left' -and -not $badgeRoot.HasAttribute('Width')) `
    'Badges must size to content rather than stretch or use a fixed width.'
Assert-Condition ($badgeRoot.GetAttribute('MaxWidth') -eq '{StaticResource SectionBadgeMaxWidth}') `
    'Badges must cap their content width.'
$badgeLabel = $badgeRoot.SelectSingleNode(".//*[local-name()='TextBlock']")
Assert-Condition ($badgeLabel.GetAttribute('TextTrimming') -eq 'CharacterEllipsis' -and
    $badgeLabel.GetAttribute('TextWrapping') -eq 'NoWrap') 'Long badge labels must trim to one line.'

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
foreach ($key in @('AccentButtonStyle', 'SubtleButtonStyle', 'DefaultButtonStyle', 'DefaultContentDialogStyle',
    'CaptionTextBlockStyle', 'BodyTextBlockStyle', 'BodyStrongTextBlockStyle', 'BodyLargeTextBlockStyle', 'SubtitleTextBlockStyle',
    'TitleTextBlockStyle', 'TitleLargeTextBlockStyle')) { [void]$platformStyleKeys.Add($key) }
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

# Neutral text, fills, strokes, and materials belong to WinUI. The only WinUI keys this dictionary may redefine
# are the accent ramp and the accent button, and only in Light and Dark so the platform owns High Contrast.
$allowedPlatformOverride = '^(?:SystemAccentColor(?:Light|Dark)?[1-3]?|AccentButton(?:Background|Foreground|BorderBrush)(?:PointerOver|Pressed)?)$'
$platformKeyPattern = '^(?:Text|Control|Subtle|Card|Layer|Solid|Smoke|Surface|Divider|System|Acrylic|Accent(?:Fill|TextFill|Button)|Button|ListViewItem|ToggleSwitch|ComboBox)[A-Za-z0-9]*$'
foreach ($themeName in @('Light', 'Dark', 'HighContrast')) {
    foreach ($key in $themeKeySets[$themeName]) {
        if ($key -notmatch $platformKeyPattern) { continue }
        Assert-Condition ($themeName -ne 'HighContrast' -and $key -match $allowedPlatformOverride) `
            "$themeName redefines the WinUI resource '$key'. Use the platform resource by name; only the accent ramp and accent button are ours."
    }
}

$themeBrushKeys = @($tokenKeys | Where-Object { $_ -match 'Brush$' -and $_ -notmatch $allowedPlatformOverride })
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
    'AppAccentBrush' = 'SystemColorHighlightColor'
    'AppOnAccentBrush' = 'SystemColorHighlightTextColor'
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
foreach ($file in @((Get-Item -LiteralPath $tokenPath)) + $xamlFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($reference in [regex]::Matches($content, '\{StaticResource\s+([A-Za-z_][A-Za-z0-9_]*)\}')) {
        $key = $reference.Groups[1].Value
        if ($key -match '(CornerRadius|Padding|Margin|Spacing|Threshold|Width|Height|Gap|Size|Thickness|FontSize|LineHeight)$') {
            Assert-Condition ($tokenKeySet.Contains($key) -or $key -in @('ControlCornerRadius', 'OverlayCornerRadius')) `
                "$($file.FullName): layout token '$key' is missing from DesignTokens.xaml."
        }
    }
}
foreach ($key in @('LayerFillColorDefaultBrush', 'LayerFillColorAltBrush', 'LayerFillColorDisabledBrush',
    'CardBackgroundFillColorDefaultBrush', 'CardBackgroundFillColorSecondaryBrush',
    'CardBackgroundFillColorTertiaryBrush', 'CardStrokeColorDefaultBrush', 'AcrylicInAppFillColorDefaultBrush')) {
    Assert-Condition (-not $tokenKeySet.Contains($key)) "Material resource '$key' must remain owned by WinUI so Mica layering and accessibility fallbacks work."
}
foreach ($page in @('DictationPage', 'MainPage', 'ModelManagementPage', 'CustomWordsPage', 'MeetingsPage')) {
    [xml]$pageDocument = Get-Content -LiteralPath (Join-Path $RepoRoot "Ansible\$page.xaml")
    $canvas = $pageDocument.SelectSingleNode('//*[local-name()="PageCanvas"]')
    Assert-Condition ($null -ne $canvas -and $canvas.HasAttribute('Title')) "$page must use the shared PageCanvas title and action layout."
    Assert-Condition ($pageDocument.SelectNodes('//*[@Style="{StaticResource PageTitleStyle}"]').Count -eq 0) "$page duplicates the shared page heading."
}
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

# One scale. Text sizes come from WinUI's type ramp (its TextBlock styles, or the ramp tokens for controls that take
# a FontSize). Radii are WinUI's ControlCornerRadius and OverlayCornerRadius. A fully round shape is the one literal:
# half the element's own literal Height. Spacing sits on the 4 DIP grid; 1 and 2 are allowed for hairlines and optical nudges. AppMark.xaml is
# exact brand geometry and is exempt.
$iconSizes = @('12', '16', '20', '24', '32', '48')
$spacingProperties = @('Margin', 'Padding', 'Spacing', 'RowSpacing', 'ColumnSpacing')
function Test-OnGrid {
    param([string]$Value)
    foreach ($part in $Value.Split(',')) {
        $number = 0.0
        if (-not [double]::TryParse($part.Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) { return $true }
        $size = [Math]::Abs($number)
        if ($size -notin @(0, 1, 2) -and ($size % 4) -ne 0) { return $false }
    }
    $true
}
function Test-ScaleValue {
    param([string]$File, [string]$Element, [string]$Property, [string]$Value, [string]$Height)
    if ($Value -notmatch '^-?[0-9][0-9.,\s-]*$') { return }
    if ($Property -eq 'FontSize') {
        Assert-Condition ($Element -eq 'FontIcon' -and $Value -in $iconSizes) `
            "${File}: <$Element> FontSize='$Value' is off the scale. Use a WinUI TextBlock style or a ramp token; icons use 12, 16, 20, 24, 32, or 48."
    } elseif ($Property -eq 'CornerRadius') {
        $half = 0.0
        $round = [double]::TryParse($Height, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$half) -and
            [double]$Value -eq $half / 2
        Assert-Condition $round "${File}: <$Element> CornerRadius='$Value' is a literal. Use ControlCornerRadius or OverlayCornerRadius; a fully round shape uses half its own literal Height."
    } elseif ($Property -in $spacingProperties) {
        Assert-Condition (Test-OnGrid $Value) "${File}: <$Element> $Property='$Value' is off the 4 DIP grid."
    }
}
foreach ($file in @((Get-Item -LiteralPath $tokenPath)) + $xamlFiles) {
    if ($file.Name -eq 'AppMark.xaml') { continue }
    [xml]$scaleDocument = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($node in $scaleDocument.SelectNodes('//*')) {
        if ($node.LocalName -eq 'Setter') {
            Test-ScaleValue $file.Name $node.ParentNode.GetAttribute('TargetType') $node.GetAttribute('Property') $node.GetAttribute('Value')
            continue
        }
        foreach ($attribute in $node.Attributes) {
            Test-ScaleValue $file.Name $node.LocalName $attribute.LocalName $attribute.Value $node.GetAttribute('Height')
        }
        $resourceKey = $node.GetAttribute('Key', $namespaceManager.LookupNamespace('x'))
        if ($node.LocalName -eq 'Thickness' -and $resourceKey -notmatch 'BorderThickness$|DividerThickness$') {
            Assert-Condition (Test-OnGrid $node.InnerText) "$($file.Name): Thickness token '$resourceKey' ($($node.InnerText)) is off the 4 DIP grid."
        } elseif ($node.LocalName -eq 'Double' -and $resourceKey -match '(Gap|Spacing|Inset)$') {
            Assert-Condition (Test-OnGrid $node.InnerText) "$($file.Name): spacing token '$resourceKey' ($($node.InnerText)) is off the 4 DIP grid."
        } elseif ($node.LocalName -eq 'CornerRadius' -and $resourceKey) {
            Assert-Condition $false "$($file.Name): radius token '$resourceKey' duplicates the WinUI radii."
        } elseif ($node.LocalName -eq 'Double' -and $resourceKey -match 'FontSize$') {
            Assert-Condition ($node.InnerText -in @('12', '14', '18', '20', '28', '40')) "$($file.Name): '$resourceKey' ($($node.InnerText)) is not a WinUI ramp size."
        }
    }
}

# XAML compiles with an unknown StaticResource key and then fails when the page loads. Every key a view names must
# be defined in this app's XAML or be one of the WinUI resources listed here.
$platformResourceKeys = @('ControlCornerRadius', 'OverlayCornerRadius') + @($platformStyleKeys)
$definedKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($key in $tokenKeys + $platformResourceKeys) { [void]$definedKeys.Add($key) }
foreach ($file in $xamlFiles) {
    foreach ($match in [regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw), 'x:Key="([^"]+)"')) {
        [void]$definedKeys.Add($match.Groups[1].Value)
    }
}
foreach ($file in @((Get-Item -LiteralPath $tokenPath)) + $xamlFiles) {
    foreach ($match in [regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw), '(?:\{StaticResource\s+|ResourceKey=")([A-Za-z_][A-Za-z0-9_]*)')) {
        Assert-Condition $definedKeys.Contains($match.Groups[1].Value) `
            "$($file.Name): StaticResource '$($match.Groups[1].Value)' is not defined in the app's XAML or the known WinUI resources."
    }
}

# A token nothing references is dead. Remove it instead of keeping it for later. XAML references a key through a
# resource markup extension; C# through a quoted key. A key's own x:Key definition is not a reference.
$xamlReferenceText = (@($xamlFiles) + @(Get-Item -LiteralPath $tokenPath) |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$codeReferenceText = ($csharpFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
foreach ($key in $tokenKeys | Sort-Object -Unique) {
    if ($key -in @('Light', 'Dark', 'HighContrast') -or $key -match $allowedPlatformOverride) { continue }
    $escaped = [regex]::Escape($key)
    Assert-Condition ($xamlReferenceText -match "(?:Resource |ResourceKey=`")$escaped\b" -or $codeReferenceText -match "`"$escaped`"") `
        "Token '$key' is not referenced anywhere. Remove it."
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
