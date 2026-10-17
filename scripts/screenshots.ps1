# The create_release_pr workflow's take_screenshots: retakes the editor screenshots of the user guide (site/guide/img:
# loop-editor.png, macro-editor.png, macro-editor-code.png, condition-editor.png, remap-editor.png; the main window on the AI page, ai-page.png)
# and the home page's (site/assets/loops.png)
# from the running development build (started before with scripts/dev.ps1 -Screenshots: PUPPYMACRO_SCREENSHOTS=1, so it
# shows the sample data and no development build marks), on a screen of 1920 x 1080 (scripts/screen-resolution.ps1).
# It drives the UI with UI Automation: do not use the mouse or keyboard while it runs. At the end (also after an
# error) it closes that PuppyMacro, so the PC is free again when it returns.
# Each window is captured alone (its own image, PrintWindow: nothing behind it shows), at its visible edges, with its
# rounded corners transparent, and saved at 100% scale.
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
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
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

# When it ends, also on an error: PuppyMacro is closed (the process, not only its window to the system tray).
function ClosePuppyMacro {
  Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
  Write-Host "Closed PuppyMacro ($($proc.Path))"
}
trap { ClosePuppyMacro; break }
$main = WaitFor { $A::FromHandle($proc.MainWindowHandle) } 'main window'
Start-Sleep -Seconds 2

function OpenEditor($itemText, $title) {
  # The list's row is named after its item (ui.md, as the end-to-end tests' ItemRow finds it), in List, Table or Grid; a
  # click on it opens its editor. A real click near its left end (its icon and name, away from its buttons): the library's
  # SettingsCard has no Invoke pattern. The main window hides while the editor is open.
  $row = WaitFor { $main.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
      (New-Object $PC($A::NameProperty, $itemText)),
      (New-Object System.Windows.Automation.OrCondition(
        (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)),
        (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))))) } "the row of $itemText"
  [W]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
  $r = $row.Current.BoundingRectangle
  [W]::SetCursorPos([int]($r.Left + 40), [int]($r.Top + $r.Height / 2)) | Out-Null
  [W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero) # left down
  [W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero) # left up
  # The editor's title is "Edit loop - <its name>".
  WaitFor { $A::RootElement.FindAll($TS::Children, (New-Object $PC($A::ProcessIdProperty, $proc.Id))) |
      Where-Object { $_.Current.Name -eq $title -or $_.Current.Name.StartsWith("$title - ") } |
      Select-Object -First 1 } $title
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
  SaveWindow $h $file
  # Cancel closes the editor; in the Code view the same button is "Discard changes" and goes back to
  # the Form view first, so it is pressed again while the editor is still open.
  for ($i = 0; $i -lt 2; $i++) {
    $cancel = $window.FindFirst($TS::Descendants, (New-Object $PC($A::AutomationIdProperty, 'CancelButton')))
    if (-not $cancel) { break }
    try { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { break }
    Start-Sleep -Milliseconds 800
  }
}

# The window's own image (PrintWindow with PW_RENDERFULLCONTENT, as DWM draws it), not the screen's pixels:
# nothing behind the window shows, at its rounded corners or through its border. Cut to its visible edges
# (DWMWA_EXTENDED_FRAME_BOUNDS, without the invisible resize borders), the corners outside Windows 11's 8 px rounding
# transparent, then scaled to 100%.
function SaveWindow($h, $file) {
  $wr = New-Object W+RECT
  [W]::GetWindowRect($h, [ref]$wr) | Out-Null
  $full = New-Object System.Drawing.Bitmap ($wr.R - $wr.L), ($wr.B - $wr.T), ([System.Drawing.Imaging.PixelFormat]::Format32bppRgb)
  $g = [System.Drawing.Graphics]::FromImage($full)
  $hdc = $g.GetHdc()
  $printed = [W]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT
  $g.ReleaseHdc($hdc)
  $g.Dispose()
  if (-not $printed) { throw "PrintWindow failed for $file" }

  $r = Bounds $h
  $w = $r.R - $r.L; $hgt = $r.B - $r.T
  $scale = [W]::GetDpiForWindow($h) / 96.0
  $radius = 8 * $scale
  $shape = New-Object System.Drawing.Drawing2D.GraphicsPath
  $shape.AddArc(0, 0, 2 * $radius, 2 * $radius, 180, 90)
  $shape.AddArc($w - 2 * $radius, 0, 2 * $radius, 2 * $radius, 270, 90)
  $shape.AddArc($w - 2 * $radius, $hgt - 2 * $radius, 2 * $radius, 2 * $radius, 0, 90)
  $shape.AddArc(0, $hgt - 2 * $radius, 2 * $radius, 2 * $radius, 90, 90)
  $shape.CloseFigure()
  $bmp = New-Object System.Drawing.Bitmap $w, $hgt, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $brush = New-Object System.Drawing.TextureBrush $full
  $brush.TranslateTransform(-($r.L - $wr.L), -($r.T - $wr.T))
  $g.FillPath($brush, $shape)
  $brush.Dispose(); $shape.Dispose(); $g.Dispose(); $full.Dispose()

  if ($scale -ne 1) {
    # Same scale as the other guide screenshots (100%).
    $sw = [int][Math]::Round($w / $scale); $sh = [int][Math]::Round($hgt / $scale)
    $small = New-Object System.Drawing.Bitmap $sw, $sh, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
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

# The condition window of the sample's If: its row selected, then Edit action.
$editor = OpenEditor 'Sample: accept when ready' 'Edit macro'
$list = WaitFor { $editor.FindFirst($TS::Descendants, (New-Object $PC($A::AutomationIdProperty, 'ActionList'))) } 'ActionList'
$rows = $list.FindAll($TS::Children, (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
$rows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$edit = WaitFor { $editor.FindFirst($TS::Descendants, (New-Object $PC($A::AutomationIdProperty, 'EditActionButton'))) } 'EditActionButton'
$edit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
# UI Automation shows a window under the window that owns it: the condition window is inside the macro editor.
$condition = WaitFor { $editor.FindAll($TS::Children, (New-Object $PC($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))) |
    Where-Object { $_.Current.Name.StartsWith('Edit condition - ') } | Select-Object -First 1 } 'Edit condition'
Start-Sleep -Seconds 2 # its check of the screen
Capture $condition (Join-Path $OutDir 'condition-editor.png')
# Then the macro editor under it closes too.
$editor.FindFirst($TS::Descendants, (New-Object $PC($A::AutomationIdProperty, 'CancelButton'))).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 800

$remaps = WaitFor { ByName $main 'Remap' } 'Remap page'
$remaps.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 1
$editor = OpenEditor 'Sample: Caps Lock to Esc' 'Edit remap'
Capture $editor (Join-Path $OutDir 'remap-editor.png')

$loops = ByName $main 'Loops'
$loops.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

# Home page: the whole main window on the Loops page, title bar included, at 800 x 800 (100% scale: the main
# window's width, so the cards keep one line). The main window gets its size back afterwards.
$h = [IntPtr]$main.Current.NativeWindowHandle
$scale = [W]::GetDpiForWindow($h) / 96.0
$transform = $main.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
$size = $main.Current.BoundingRectangle   # the whole window, invisible borders included
$restoreWidth = $size.Width; $restoreHeight = $size.Height
[W]::SetForegroundWindow($h) | Out-Null
# Resize to the wanted visible size, then correct by what is measured (invisible borders, rounding).
$wantWidth = [Math]::Round(800 * $scale)
$wantHeight = [Math]::Round(800 * $scale)
for ($pass = 0; $pass -lt 3; $pass++) {
  $r = Bounds $h
  $dw = $wantWidth - ($r.R - $r.L); $dh = $wantHeight - ($r.B - $r.T)
  if ($dw -eq 0 -and $dh -eq 0) { break }
  $now = $main.Current.BoundingRectangle
  $transform.Resize($now.Width + $dw, $now.Height + $dh)
  Start-Sleep -Milliseconds 800
}
# Selecting Loops through UI Automation gave its side rail item the keyboard focus (a frame and its tooltip):
# the window takes it back, with no frame.
$main.SetFocus()
Start-Sleep -Milliseconds 800
SaveWindow $h (Join-Path (Split-Path -Parent $PSScriptRoot) 'site/assets/loops.png')

# The guide's AI page (ai.html, "Set it up"): the same window and size on the AI page, as this machine shows it.
$aiPage = WaitFor { ByName $main 'AI' } 'AI page'
$aiPage.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 1
WaitFor { ByName $main 'Allow AI access' } 'the AI page shown' | Out-Null
$main.SetFocus()
Start-Sleep -Milliseconds 800
SaveWindow $h (Join-Path $OutDir 'ai-page.png')
$transform.Resize($restoreWidth, $restoreHeight)

ClosePuppyMacro
