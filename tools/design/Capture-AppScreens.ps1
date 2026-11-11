<#
.SYNOPSIS
Builds the Debug app, launches it per theme, and captures the fixed UX review matrix.

.DESCRIPTION
The matrix is 5 pages x light/dark x wide/narrow, each long page at one stop per viewport, plus the welcome
lesson and a saved meeting transcript window. Captures land in .runtime\ux\<Round>. The matrix must stay
the same between review rounds so scores stay comparable. A page that fails to load fails the run, so this
is also the runtime check for unresolved XAML resources.

Requires the winapp CLI and Windows Developer Mode. Replaces the running development instance.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Round,
    [ValidateSet('light', 'dark')][string[]]$Theme = @('light', 'dark'),
    # Limits the run to some pages, or to 'extras' (welcome lesson and meeting window), for a quick local probe.
    # Review rounds use the full matrix.
    [string[]]$Only,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildOutput = Join-Path $repo 'Ansible\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64'
$layout = Join-Path $repo '.runtime\ux\layout'
$out = Join-Path $repo ".runtime\ux\$Round"
New-Item -ItemType Directory -Force $out | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
if (-not ('DesignCapture' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesignCapture {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
}

function Stop-DevelopmentApp {
    Get-Process Ansible -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -Confirm:$false
    Start-Sleep -Milliseconds 1500
}

function Invoke-Ui {
    param([string[]]$Arguments, [switch]$Optional)
    $output = & winapp ui @Arguments 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $Optional) { throw "winapp ui $($Arguments -join ' ') failed: $output" }
    $output
}

function Set-WindowSize {
    param([IntPtr]$Handle, [int]$WidthDips, [int]$HeightDips)
    $context = [DesignCapture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
    try {
        $scale = [DesignCapture]::GetDpiForWindow($Handle) / 96.0
        # Park the pointer on the title bar so no control renders its hover state.
        [DesignCapture]::SetWindowPos($Handle, [IntPtr]::Zero, 0, 40, [int]($WidthDips * $scale), [int]($HeightDips * $scale), 0x0014) | Out-Null
        [DesignCapture]::SetCursorPos([int](400 * $scale), 40 + [int](20 * $scale)) | Out-Null
    } finally { [DesignCapture]::SetThreadDpiAwarenessContext($context) | Out-Null }
    Start-Sleep -Milliseconds 700
}

function Save-Window {
    param([IntPtr]$Handle, [string]$Name)
    Invoke-Ui @('screenshot', '-w', "$Handle", '-o', (Join-Path $out "$Name.png"), '--json') | Out-Null
    Write-Output "Captured $Name"
}

function Get-PageScroller {
    param([IntPtr]$Handle)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PageViewport')
    # The automation tree is briefly empty while a page reflows after a resize or navigation.
    $deadline = (Get-Date).AddSeconds(8)
    do {
        $viewport = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if (-not $viewport) { Start-Sleep -Milliseconds 400 }
    } until ($viewport -or (Get-Date) -gt $deadline)
    if (-not $viewport) { throw 'PageViewport was not found. The page did not load.' }
    $pattern = $null
    if ($viewport.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) { $pattern }
}

# One stop per 90% of a viewport, top to bottom. -1 means the page does not scroll.
function Get-ScrollStops {
    param([IntPtr]$Handle)
    $scroller = Get-PageScroller $Handle
    if (-not $scroller -or -not $scroller.Current.VerticallyScrollable) { return @(-1) }
    $view = $scroller.Current.VerticalViewSize / 100.0
    $step = 90.0 * $view / (1 - $view)
    $stops = @(0)
    for ($percent = $step; $percent -lt 100; $percent += $step) { $stops += $percent }
    $stops + 100 | Select-Object -First 8
}

function Set-PageScroll {
    param([IntPtr]$Handle, [double]$Percent)
    (Get-PageScroller $Handle).SetScrollPercent(-1, $Percent)
}

# Home says no model is loaded until the session finishes starting. Capturing earlier records a transient state.
function Wait-SessionReady {
    param([IntPtr]$Handle)
    Start-Sleep -Seconds 4
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    $starting = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'No speech model loaded')
    $deadline = (Get-Date).AddSeconds(30)
    while ($root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $starting) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 2
}

if (-not $SkipBuild) {
    Stop-DevelopmentApp
    dotnet build (Join-Path $repo 'Ansible\Ansible.csproj') -c Debug -p:Platform=x64 -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
}

$pages = [ordered]@{ home = 'DictationNav'; meetings = 'MeetingsNav'; models = 'ModelsNav'; dictionary = 'CustomWordsNav'; settings = 'SettingsNav' }
$sizes = [ordered]@{ wide = @(1440, 960); narrow = @(860, 760) }

foreach ($mode in $Theme) {
    Stop-DevelopmentApp
    $launch = & winapp run $buildOutput --output-appx-directory $layout --detach --json --args "--design-theme=$mode" | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Launch failed: $launch" }
    $appId = ($launch.Substring($launch.IndexOf('{')) | ConvertFrom-Json).ProcessId
    $deadline = (Get-Date).AddSeconds(40)
    do {
        Start-Sleep -Milliseconds 500
        $app = Get-Process -Id $appId -ErrorAction SilentlyContinue
        if (-not $app) { throw "The app exited during startup ($mode). Check the Application event log." }
    } until ($app.MainWindowHandle -ne [IntPtr]::Zero -or (Get-Date) -gt $deadline)
    if ($app.MainWindowHandle -eq [IntPtr]::Zero) { throw "No main window after 40 seconds ($mode)." }
    $window = $app.MainWindowHandle
    Wait-SessionReady $window

    foreach ($size in $sizes.GetEnumerator()) {
        Set-WindowSize $window $size.Value[0] $size.Value[1]
        foreach ($page in $pages.GetEnumerator()) {
            if ($Only -and $page.Key -notin $Only) { continue }
            Invoke-Ui @('invoke', $page.Value, '-w', "$window", '-q') | Out-Null
            Start-Sleep -Milliseconds 900
            $name = "$($page.Key)-$mode-$($size.Key)"
            $stop = 1
            foreach ($percent in Get-ScrollStops $window) {
                if ($percent -ge 0) { Set-PageScroll $window $percent }
                Start-Sleep -Milliseconds 500
                Save-Window $window "$name-$stop"
                $stop++
            }
        }
    }

    if ($Only -and 'extras' -notin $Only) { continue }
    # Extras at the wide size: the welcome lesson and a saved meeting transcript window.
    Set-WindowSize $window $sizes.wide[0] $sizes.wide[1]
    Invoke-Ui @('invoke', 'SettingsNav', '-w', "$window", '-q') | Out-Null
    Start-Sleep -Milliseconds 900
    Invoke-Ui @('invoke', 'ShortcutLessonButton', '-w', "$window", '-q') | Out-Null
    Start-Sleep -Milliseconds 1500
    Save-Window $window "welcome-$mode-wide-1"
    # Close by pointer-style invoke. A key press would switch the app to keyboard focus visuals for later frames.
    Invoke-Ui @('invoke', 'SkipButton', '-w', "$window", '-q') | Out-Null
    Start-Sleep -Milliseconds 800

    Invoke-Ui @('invoke', 'MeetingsNav', '-w', "$window", '-q') | Out-Null
    Start-Sleep -Milliseconds 900
    $rows = Invoke-Ui @('inspect', 'MeetingGroups', '-w', "$window", '-d', '2') -Optional | Out-String
    if ($rows -match '(?m)^\s+(btn-[0-9a-z-]+) Button') {
        # A real click, so the transcript window opens in pointer mode without keyboard focus visuals.
        Invoke-Ui @('click', $Matches[1], '-w', "$window", '-q') | Out-Null
        Start-Sleep -Seconds 2
        $handles = @((Invoke-Ui @('list-windows', '-a', "$appId") | Out-String) -split "`n" |
            ForEach-Object { if ($_ -match 'HWND (\d+)') { [IntPtr][long]$Matches[1] } } |
            Where-Object { $_ -ne $window })
        if ($handles.Count -gt 0) { Save-Window $handles[0] "meeting-window-$mode-1" }
        else { Save-Window $window "meeting-detail-$mode-wide-1" }
    } else { Write-Warning 'No saved meeting row to open.' }
}

Stop-DevelopmentApp
Write-Output "Round $Round captured in $out"
Get-ChildItem -LiteralPath $out -Filter *.png | Select-Object -ExpandProperty Name
