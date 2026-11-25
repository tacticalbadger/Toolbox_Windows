<#
.SYNOPSIS
    Drives the running app through Windows UI Automation and synthetic input.

.DESCRIPTION
        tools\e2e\ui.ps1 windows                       top-level windows of the app
        tools\e2e\ui.ps1 tree [-Depth 8] [-Within <automationId>]
        tools\e2e\ui.ps1 find    -Id <automationId> | -Name <name>
        tools\e2e\ui.ps1 invoke  -Id <automationId> | -Name <name>     button, menu item, tree item
        tools\e2e\ui.ps1 expand  -Id ... | -Name ...   /  collapse / select
        tools\e2e\ui.ps1 setvalue -Id ... -Text <text>                 text boxes
        tools\e2e\ui.ps1 value   -Id ...               what a text box holds
        tools\e2e\ui.ps1 focus   -Id ... | -Name ...   puts the keyboard there
        tools\e2e\ui.ps1 press  -Id <automationId>    a button of a system dialog, by window message
                                                       (-Id 14004 is "Yes" in the RDP certificate warning)
        tools\e2e\ui.ps1 click  -Id ... | -Name ... [-Right] [-Double]  real mouse click on its centre
        tools\e2e\ui.ps1 clickat -X <px> -Y <px> [-Right] [-Double]      relative to the main window
        tools\e2e\ui.ps1 drag    -X <px> -Y <px> -ToX <px> -ToY <px>      relative to the main window
        tools\e2e\ui.ps1 keys    -Keys '<SendKeys syntax>'               e.g. '^+{]}' for Ctrl+Shift+]
        tools\e2e\ui.ps1 text    -Text 'echo hello'                      typed literally
        tools\e2e\ui.ps1 foreground

    "invoke", "expand", "select", "setvalue", "value" and "focus" use
    automation and touch neither the mouse nor the keyboard; nor does
    "press", which is for buttons that offer no pattern. "click", "drag", "keys" and
    "text" are real input: they bring the app to the front first and refuse
    to type when another program has the keyboard.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('windows', 'tree', 'find', 'invoke', 'expand', 'collapse', 'select', 'setvalue', 'value', 'focus', 'press', 'click', 'clickat', 'drag', 'keys', 'text', 'foreground')]
    [string]$Action,
    [string]$Id,
    [string]$Name,
    [string]$Within,
    [string]$Text,
    [string]$Keys,
    [int]$Depth = 8,
    [int]$X,
    [int]$Y,
    [int]$ToX,
    [int]$ToY,
    [switch]$Right,
    [switch]$Double
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
if (-not ('ToolboxE2E.Input' -as [type])) {
    Add-Type -Namespace ToolboxE2E -Name Input -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
[DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
}
[ToolboxE2E.Input]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null

$Auto = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$app = @(Get-Process -Name 'Toolbox' -ErrorAction SilentlyContinue)
if ($app.Count -eq 0) { Write-Host 'Toolbox is not running'; exit 1 }
$processIds = @($app | ForEach-Object { $_.Id })

function Get-AppWindows {
    $all = $Auto::RootElement.FindAll($TreeScope::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $all) {
        try { if ($processIds -contains $w.Current.ProcessId) { $w } } catch { }
    }
}

function Get-MainWindow {
    $main = $app | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    if (-not $main) { throw 'the app has no main window' }
    return $main.MainWindowHandle
}

# Scrolls the sidebar's tree from the top, a page at a time, until a row
# matching the condition is there. The tree is left where the row was found.
function Find-InSidebar($condition) {
    $treeCondition = New-Object System.Windows.Automation.PropertyCondition ($Auto::AutomationIdProperty, 'SidebarTree')
    foreach ($window in @(Get-AppWindows)) {
        $tree = $window.FindFirst($TreeScope::Descendants, $treeCondition)
        if (-not $tree) { continue }
        $scroll = $null
        if (-not $tree.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll)) { return $null }
        if (-not $scroll.Current.VerticallyScrollable) { return $null }
        $step = [Math]::Max(2.0, $scroll.Current.VerticalViewSize * 0.8)
        for ($at = 0.0; $at -lt 100 + $step; $at += $step) {
            $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, [Math]::Min(100.0, $at))
            Start-Sleep -Milliseconds 120
            $found = $tree.FindFirst($TreeScope::Descendants, $condition)
            if ($found) { return $found }
        }
        return $null
    }
    return $null
}

function Find-Element {
    $conditions = @()
    if ($Id) { $conditions += New-Object System.Windows.Automation.PropertyCondition ($Auto::AutomationIdProperty, $Id) }
    if ($Name) { $conditions += New-Object System.Windows.Automation.PropertyCondition ($Auto::NameProperty, $Name) }
    if ($conditions.Count -eq 0) { throw 'give -Id or -Name' }
    $condition = if ($conditions.Count -eq 1) { $conditions[0] } else { New-Object System.Windows.Automation.AndCondition ($conditions) }
    # Dialogs and menus are separate top-level windows; the newest comes first.
    foreach ($window in @(Get-AppWindows)) {
        $root = $window
        if ($Within) {
            $root = $window.FindFirst($TreeScope::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($Auto::AutomationIdProperty, $Within)))
            if (-not $root) { continue }
        }
        $found = $root.FindFirst(($TreeScope::Element -bor $TreeScope::Descendants), $condition)
        if ($found) { return $found }
    }
    # The sidebar only builds the rows that are on screen: one that is
    # further down exists for automation once it has been scrolled to.
    $inTree = Find-InSidebar $condition
    if ($inTree) { return $inTree }
    throw "no element with $(if ($Id) { "id '$Id'" }) $(if ($Name) { "name '$Name'" })"
}

function Describe($element) {
    try { $null = $element.Current.ControlType } catch { return "(element is gone)" }
    $c = $element.Current
    $r = $c.BoundingRectangle
    $rect = if (-not ($r -is [System.Windows.Rect]) -or $r.IsEmpty) { 'offscreen' } else { '{0},{1} {2}x{3}' -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height }
    $extra = ''
    if ($c.HelpText) { $extra += " help='$($c.HelpText)'" }
    if ($c.ItemStatus) { $extra += " status='$($c.ItemStatus)'" }
    if (-not $c.IsEnabled) { $extra += ' DISABLED' }
    "{0} '{1}' id='{2}' [{3}]{4}" -f ($c.ControlType.ProgrammaticName -replace 'ControlType\.', ''), $c.Name, $c.AutomationId, $rect, $extra
}

function Show-Tree($element, [int]$level) {
    Write-Host ((' ' * ($level * 2)) + (Describe $element))
    if ($level -ge $Depth) { return }
    $children = $element.FindAll($TreeScope::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($child in $children) { Show-Tree $child ($level + 1) }
}

function Test-Foreground {
    $front = [ToolboxE2E.Input]::GetForegroundWindow()
    $frontProcess = 0
    [ToolboxE2E.Input]::GetWindowThreadProcessId($front, [ref]$frontProcess) | Out-Null
    $hosts = @(Get-Process -Name 'msedgewebview2', 'CredentialUIBroker' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    return ($processIds -contains $frontProcess) -or ($hosts -contains $frontProcess)
}

function Set-Foreground {
    if (Test-Foreground) { return }
    $hwnd = Get-MainWindow
    if ([ToolboxE2E.Input]::IsIconic($hwnd)) { [ToolboxE2E.Input]::ShowWindow($hwnd, 9) | Out-Null }
    # UI Automation may activate a window where a plain SetForegroundWindow
    # from a background process is refused. No key is tapped to get the
    # right: a stray Alt would open the menu bar and swallow what follows.
    try { $Auto::FromHandle($hwnd).SetFocus() } catch { }
    [ToolboxE2E.Input]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 250
}

function Assert-Foreground {
    if (-not (Test-Foreground)) { throw 'another program has the keyboard; nothing was typed' }
}

function Invoke-Click([int]$px, [int]$py) {
    [ToolboxE2E.Input]::SetCursorPos($px, $py) | Out-Null
    Start-Sleep -Milliseconds 80
    $down = if ($Right) { 0x0008 } else { 0x0002 }
    $up = if ($Right) { 0x0010 } else { 0x0004 }
    $times = if ($Double) { 2 } else { 1 }
    for ($i = 0; $i -lt $times; $i++) {
        [ToolboxE2E.Input]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
        [ToolboxE2E.Input]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }
}

function Get-WindowOrigin {
    $rect = New-Object ToolboxE2E.Input+RECT
    [ToolboxE2E.Input]::GetWindowRect((Get-MainWindow), [ref]$rect) | Out-Null
    return $rect
}

switch ($Action) {
    'windows' {
        foreach ($w in @(Get-AppWindows)) { Write-Host (Describe $w) }
    }
    'tree' {
        foreach ($w in @(Get-AppWindows)) {
            $root = $w
            if ($Within) {
                $root = $w.FindFirst($TreeScope::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($Auto::AutomationIdProperty, $Within)))
                if (-not $root) { continue }
            }
            Show-Tree $root 0
        }
    }
    'find' { Write-Host (Describe (Find-Element)) }
    'invoke' {
        $element = Find-Element
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            $pattern.Invoke()
        }
        elseif ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
            $pattern.Toggle()
        }
        elseif ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $pattern.Select()
        }
        elseif ($element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
            $pattern.Expand()
        }
        else { throw "nothing to invoke on: $(Describe $element)" }
        Write-Host "invoked: $(Describe $element)"
    }
    'expand' {
        $element = Find-Element
        $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Write-Host "expanded: $(Describe $element)"
    }
    'collapse' {
        $element = Find-Element
        $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        Write-Host "collapsed: $(Describe $element)"
    }
    'select' {
        $element = Find-Element
        $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Write-Host "selected: $(Describe $element)"
    }
    'setvalue' {
        $element = Find-Element
        $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
        Write-Host "set: $(Describe $element)"
    }
    'focus' {
        $element = Find-Element
        $element.SetFocus()
        Write-Host "focused: $(Describe $element)"
    }
    'value' {
        $element = Find-Element
        Write-Host $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    }
    'press' {
        # The buttons of the RDP control's certificate warning are plain
        # windows without automation patterns. BM_CLICK presses one.
        $element = Find-Element
        $hwnd = [IntPtr]$element.Current.NativeWindowHandle
        if ($hwnd -eq [IntPtr]::Zero) { throw "no window of its own: $(Describe $element)" }
        # Described first: the dialog, and the element with it, is gone afterwards.
        $what = Describe $element
        [ToolboxE2E.Input]::PostMessage($hwnd, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Write-Host "pressed: $what"
    }
    'click' {
        $element = Find-Element
        $r = $element.Current.BoundingRectangle
        if ($r.IsEmpty) { throw "not on screen: $(Describe $element)" }
        Set-Foreground
        Invoke-Click ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
        Write-Host "clicked: $(Describe $element)"
    }
    'clickat' {
        Set-Foreground
        $o = Get-WindowOrigin
        Invoke-Click ($o.Left + $X) ($o.Top + $Y)
        Write-Host "clicked at $X,$Y"
    }
    'drag' {
        Set-Foreground
        $o = Get-WindowOrigin
        $fx = $o.Left + $X; $fy = $o.Top + $Y; $tx = $o.Left + $ToX; $ty = $o.Top + $ToY
        [ToolboxE2E.Input]::SetCursorPos($fx, $fy) | Out-Null
        Start-Sleep -Milliseconds 120
        [ToolboxE2E.Input]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        $steps = 24
        for ($i = 1; $i -le $steps; $i++) {
            [ToolboxE2E.Input]::SetCursorPos([int]($fx + ($tx - $fx) * $i / $steps), [int]($fy + ($ty - $fy) * $i / $steps)) | Out-Null
            Start-Sleep -Milliseconds 25
        }
        Start-Sleep -Milliseconds 250
        [ToolboxE2E.Input]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Write-Host "dragged $X,$Y -> $ToX,$ToY"
    }
    'keys' {
        Set-Foreground
        Assert-Foreground
        [System.Windows.Forms.SendKeys]::SendWait($Keys)
        Write-Host "sent keys: $Keys"
    }
    'text' {
        Set-Foreground
        Assert-Foreground
        $escaped = [regex]::Replace($Text, '[+^%~(){}\[\]]', { param($m) '{' + $m.Value + '}' })
        [System.Windows.Forms.SendKeys]::SendWait($escaped)
        Write-Host "typed: $Text"
    }
    'foreground' {
        Set-Foreground
        Write-Host 'brought to the front'
    }
}
