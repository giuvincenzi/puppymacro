# make screenshots: retakes the editor screenshots of the user guide (site/guide/img: loop-editor.png,
# macro-editor.png, macro-editor-code.png, remap-editor.png) and the home page's (site/assets/loops.png) from the
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
  # The card's Edit button is on the same row (the main window hides while the editor is open).
  $buttons = $main.FindAll($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
      (New-Object $PC($A::NameProperty, 'Edit')),
      (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
  $edit = $buttons | Where-Object {
    [Math]::Abs($_.Current.BoundingRectangle.Y + $_.Current.BoundingRectangle.Height / 2 - $y) -lt 40 } |
    Select-Object -First 1
  $edit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  WaitFor { $A::RootElement.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
      (New-Object $PC($A::NameProperty, $title)),
      (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))))) } $title
}

function Bounds($h) {
  $r = New-Object W+RECT
  [W]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null   # DWMWA_EXTENDED_FRAME_BOUNDS
  $r
}

function Capture($window, $file) {
  $h = [IntPtr]$window.Current.NativeWindowHandle
  [W]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 1200
  $r = Bounds $h
  SaveRegion $h $r.L $r.T $r.R $r.B $file
  # Cancel closes the editor; in the Code view the same button is "Discard changes" and goes back to
  # the Form view first, so it is pressed again while the editor is still open.
  for ($i = 0; $i -lt 2; $i++) {
    $cancel = $window.FindFirst($TS::Descendants, (New-Object $PC($A::AutomationIdProperty, 'CancelButton')))
    if (-not $cancel) { break }
    try { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { break }
    Start-Sleep -Milliseconds 800
  }
}

function SaveRegion($h, [int]$left, [int]$top, [int]$right, [int]$bottom, $file) {
  $w = $right - $left; $hgt = $bottom - $top
  $bmp = New-Object System.Drawing.Bitmap $w, $hgt
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $w, $hgt))
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

$remaps = WaitFor { ByName $main 'Remap' } 'Remap page'
$remaps.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 1
$editor = OpenEditor 'Sample: Caps Lock to Esc' 'Edit remap'
Capture $editor (Join-Path $OutDir 'remap-editor.png')

$loops = ByName $main 'Loops'
$loops.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

# Home page: the Loops page at 800 x 750 (100% scale: the main window's width, so the cards keep one
# line), below the title bar and the development
# strip (the home page draws its own window bar). The main window gets its size back afterwards.
$h = [IntPtr]$main.Current.NativeWindowHandle
$scale = [W]::GetDpiForWindow($h) / 96.0
$transform = $main.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
$size = $main.Current.BoundingRectangle   # the whole window, invisible borders included
$restoreWidth = $size.Width; $restoreHeight = $size.Height
$strip = WaitFor { ByName $main 'Development build' } 'Development build strip'
[W]::SetForegroundWindow($h) | Out-Null
# Resize to the wanted visible size, then correct by what is measured (invisible borders, rounding).
$wantWidth = [Math]::Round(800 * $scale)
$wantHeight = [Math]::Round(750 * $scale)
for ($pass = 0; $pass -lt 3; $pass++) {
  $r = Bounds $h
  $top = [Math]::Round($strip.Current.BoundingRectangle.Bottom + 3 * $scale)   # the strip's bottom padding
  $dw = $wantWidth - ($r.R - $r.L); $dh = $wantHeight - ($r.B - $top)
  if ($dw -eq 0 -and $dh -eq 0) { break }
  $now = $main.Current.BoundingRectangle
  $transform.Resize($now.Width + $dw, $now.Height + $dh)
  Start-Sleep -Milliseconds 800
}
Start-Sleep -Milliseconds 800
$r = Bounds $h
$top = [Math]::Round($strip.Current.BoundingRectangle.Bottom + 3 * $scale)
SaveRegion $h $r.L $top $r.R $r.B (Join-Path (Split-Path -Parent $PSScriptRoot) 'site/assets/loops.png')
$transform.Resize($restoreWidth, $restoreHeight)
