using System;
using System.Collections;
using System.Windows.Interop;
using PuppyMacro.Native;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// Borderless, always-on-top, click-through window that shows the overlay panel.
/// It never takes focus away from the fullscreen app.
/// </summary>
public partial class OverlayPanelWindow
{
    internal OverlayPanelWindow(IList loops, IList macros)
    {
        InitializeComponent();
        PanelView.Bind(loops, macros);
        SourceInitialized += OnSourceInitialized;
    }

    public void SetHotkeyLabels(string exitHotkey, string stopAllHotkey) => PanelView.SetHotkeyLabels(exitHotkey, stopAllHotkey);

    public void SetBackgroundOpacity(int percent) => PanelView.SetBackgroundOpacity(percent);

    public void RefreshList() => PanelView.RefreshList();

    /// <summary>Clickable rows of the panel, in physical screen pixels.</summary>
    internal System.Collections.Generic.List<Services.PanelTarget> GetClickTargets() => PanelView.GetClickTargets();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Click-through + never activated + hidden from Alt+Tab.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TRANSPARENT
                 | NativeMethods.WS_EX_LAYERED
                 | NativeMethods.WS_EX_NOACTIVATE
                 | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }
}
