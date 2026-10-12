# Sets the main screen's resolution (Windows' ChangeDisplaySettings), then checks it. Used by the Screenshots workflow:
# a GitHub runner's screen is 1024 x 768, smaller than the editors the guide shows (up to 1420 x 820), and a window that
# does not fit is made smaller (WindowFit), so its screenshot would not look like on a contributor's PC.
param([int]$Width = 1920, [int]$Height = 1080)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Display {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
    public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels;
    public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
  public static int Set(int width, int height) {
    var dm = new DEVMODE();
    dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
    if (!EnumDisplaySettings(null, -1, ref dm)) return -100; // ENUM_CURRENT_SETTINGS
    dm.dmPelsWidth = width;
    dm.dmPelsHeight = height;
    dm.dmFields = 0x80000 | 0x100000; // DM_PELSWIDTH | DM_PELSHEIGHT
    return ChangeDisplaySettings(ref dm, 0);
  }
}
"@
$result = [Display]::Set($Width, $Height)
Add-Type -AssemblyName System.Windows.Forms
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "ChangeDisplaySettings: $result. Screen: $($bounds.Width) x $($bounds.Height)"
if ($bounds.Width -lt $Width -or $bounds.Height -lt $Height) {
    throw "The screen is $($bounds.Width) x $($bounds.Height), not $Width x $Height: the screenshots would not match the guide's."
}
