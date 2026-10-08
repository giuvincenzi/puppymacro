# make screenshots: retakes the editor screenshots of the user guide (site/guide/img: loop-editor.png,
# macro-editor.png, macro-editor-code.png) from the
# running development build (start it first with make dev, so it shows the sample data).
# It drives the UI with UI Automation: do not use the mouse or keyboard while it runs.
# Each window is captured alone, at its visible edges, and saved at 100% scale.
param([string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'site/guide/img'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
}
"@
[W]::SetProcessDPIAware() | Out-Null
$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$PC = [System.Windows.Automation.PropertyCondition]

function WaitFor([scriptblock]$get, [string]$what) {
  for ($i = 0; $i -lt 100; $i++) { $r = & $get; if ($r) { return $r }; Start-Sleep -Milliseconds 200 }
  throw "Not found: $what"
}
function ByName($root, $name) { $root.FindFirst($TS::Descendants, (New-Object $PC($A::NameProperty, $name))) }

$proc = WaitFor { Get-Process PuppyMacro -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1 } 'PuppyMacro'
$main = WaitFor { $A::FromHandle($proc.MainWindowHandle) } 'main window'
Start-Sleep -Seconds 2

function OpenEditor($itemText, $title) {
  $text = WaitFor { ByName $main $itemText } $itemText
  $y = $text.Current.BoundingRectangle.Y
  $buttons = $main.FindAll($TS::Descendants, (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
  $more = $buttons | Where-Object {
    $_.Current.Name -eq '' -and $_.Current.AutomationId -eq '' -and
    [Math]::Abs($_.Current.BoundingRectangle.Y + $_.Current.BoundingRectangle.Height / 2 - $y) -lt 40 } |
    Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -Last 1
  $more.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $edit = WaitFor { $A::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
      (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)),
      (New-Object $PC($A::NameProperty, 'Edit'))))) } 'Edit menu item'
  $edit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  WaitFor { $A::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
      (New-Object $PC($A::NameProperty, $title)),
      (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))) } $title
}

function Capture($window, $file) {
  $h = [IntPtr]$window.Current.NativeWindowHandle
  [W]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 1200
  $r = New-Object W+RECT
  [W]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null   # DWMWA_EXTENDED_FRAME_BOUNDS
  $w = $r.R - $r.L; $hgt = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap $w, $hgt
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hgt))
  $g.Dispose()
  $scale = [W]::GetDpiForWindow($h) / 96.0
  if ($scale -ne 1) {
    # Same scale as the other guide screenshots (100%).
    $sw = [int][Math]::Round($w / $scale); $sh = [int][Math]::Round($hgt / $scale)
    $small = New-Object System.Drawing.Bitmap $sw, $sh
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.DrawImage($bmp, 0, 0, $sw, $sh)
    $g2.Dispose(); $bmp.Dispose(); $bmp = $small
  }
  $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host "Saved $file ($w x $hgt, scale $scale)"
  $cancel = ByName $window 'Cancel'
  $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 800
}

$editor = OpenEditor 'Sample: click every second' 'Edit loop'
Capture $editor (Join-Path $OutDir 'loop-editor.png')

$macros = WaitFor { ByName $main 'Macros' } 'Macros page'
$macros.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 1
$editor = OpenEditor 'Sample: type and confirm' 'Edit macro'
# Select the last action (the sample's named action "Send"), so the selection bar shows as in the guide.
$list = WaitFor { $editor.FindFirst($TS::Descendants, (New-Object $PC($A::AutomationIdProperty, 'ActionList'))) } 'ActionList'
$rows = $list.FindAll($TS::Children, (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
$rows[$rows.Count - 1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Capture $editor (Join-Path $OutDir 'macro-editor.png')

# The same macro in the Code view, once the code editor has loaded and checked it.
$editor = OpenEditor 'Sample: type and confirm' 'Edit macro'
$code = WaitFor { $editor.FindFirst($TS::Descendants, (New-Object $PC($A::AutomationIdProperty, 'CodeViewButton'))) } 'CodeViewButton'
$code.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
WaitFor { ByName $editor 'No problems.' } 'the Code view checked' | Out-Null
Start-Sleep -Seconds 1
Capture $editor (Join-Path $OutDir 'macro-editor-code.png')

$loops = ByName $main 'Loops'
$loops.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
