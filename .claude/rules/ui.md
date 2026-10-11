---
paths:
  - "PuppyMacro/**/*.xaml"
  - "PuppyMacro/**/*.xaml.cs"
  - "PuppyMacro/MainWindow.Updates.cs"
  - "PuppyMacro/Views/**"
---

# UI

- Before a UI change, follow "UI changes" in `working-rules.md` (ask whether the user wants
  visual proposals as a Design artifact, wait for approval, then implement).

## Standard components only (strict)

The app must look and behave exactly like Windows 11. It is built with
**iNKORE.UI.WPF.Modern**, the WPF port of the WinUI controls and of the Windows Community
Toolkit's SettingsCard / SettingsExpander that Windows Settings uses.

- **The reference is the library's documentation:**
  https://docs.inkore.net/en-us/ui-wpf-modern/introduction (setup:
  https://docs.inkore.net/en-us/ui-wpf-modern/onboarding, every component by category:
  https://docs.inkore.net/en-us/ui-wpf-modern/components), and its Gallery app in the
  repository https://github.com/iNKORE-NET/UI.WPF.Modern (source of every control and of the
  Gallery pages). Before building or changing a screen, look up the component there and use it
  as documented; check the source when the docs do not say.
- Every normal window (main window, editors, dialogs, the record prompt) sets
  `ui:WindowHelper.UseModernWindowStyle="True"` and `ui:WindowHelper.SystemBackdropType="Mica"`
  (windows are not styled globally). The app targets `net10.0-windows10.0.18362.0`: with a lower
  Windows version the library cannot follow the Windows theme.
- The windows drawn over other apps (`OverlayPanelWindow`, `FloatingButtonWindow`,
  `PlacementWindow`, `PickPointWindow`, `RecordingBarWindow`, `CountdownWindow`) are transparent,
  borderless and topmost, with a fixed dark look of their own in every theme: they sit on top of
  fullscreen apps.
- Use its controls as they are, with their default styles: `SettingsCard`, `SettingsExpander`,
  `ListView`, `CommandBar` / `AppBarButton`, `ToggleSwitch`, `NumberBox`, `InfoBar`, `InfoBadge`,
  `ContentDialog`, `HyperlinkButton`, buttons (`AccentButtonStyle` for the main action), `FontIcon`
  with `SegoeFluentIcons` (or `FluentSystemIcons` where a Regular / Filled pair is needed), the text styles
  (`TitleTextBlockStyle`, `BodyStrongTextBlockStyle`, …) and the theme brushes.
- Lay out pages as the library's Gallery and Windows Settings do (for example
  `SimpleStackPanel Spacing="4"` between cards, a `BodyStrongTextBlockStyle` title above each
  section). Take sizes and spacing from there, never invent them.
- **No custom control templates, no restyled controls, no own colors or sizes for standard
  controls, no hand cursors** (Windows shows what can be clicked by lighting it up, not with
  the cursor).
- Known library bug (0.10.2, rewritten in its `main` in January 2026, not released yet): a
  `FontIcon` whose `Icon` changes after it is shown (style trigger, binding) can set its own `Icon`
  to null as a local value (`FontIconSource.UpdateIconData` compares the font family by
  reference), and from then on it never changes again. So **never change a `FontIcon`'s `Icon`**:
  put one `FontIcon` per icon and switch their `Visibility` (as the side rail, the cards' Play / Stop
  and the macro editor's group arrow do).
- Known library bug (0.10.2, still in its `main`): `SettingsCard` gives a `NumberBox` inside it
  `{StaticResource SettingsCardContentMinWidth}`, which is only in the theme dictionaries, so the
  layout throws "UnsetValue is not a valid value for property MinWidth" (a theme style cannot see
  the app's resources). So **every `NumberBox` inside a `SettingsCard` sets
  `MinWidth="{StaticResource SettingsCardContentMinWidth}"`** (defined in `App.xaml` with the
  library's value, 120): a local value wins over that style. The unit test
  `ControlResourcesTests` checks every XAML file for it; when updating the library, check whether
  the bug is gone.
- Approved by the user (custom on purpose):
  - Drag to move: an accent line (`AccentTextFillColorPrimaryBrush`) where the dragged items go, a
    3 px rounded Border above or below a list card (`DropBefore` / `DropAfter`), a 2 px border on a
    macro editor row.
  - Settings > Overlay > Opacity: a small preview of the panel (`OpacityPreviewPanel`) with the
    overlay panel's fixed dark colors, so the chosen opacity can be seen.
  - Development builds: the orange "Development build" strip under the title bar (fixed colors,
    `build-and-release.md`).
  - Disabled loops, macros and remaps in the lists: the card's icon, header and description use
    `TextFillColorDisabledBrush`, while the card stays enabled (its switch and More options menu must
    keep working; the card's own disabled state would block them).
  - `Views/SwitchSettingsExpander`: a `SettingsExpander` with a switch in its header whose options
    open only while the switch is on (`CanExpand`): otherwise no arrow and the header does not open
    it. Use it for every group whose switch unlocks its options (today: Show in overlay, floating button, Sound
    and Specific app in the loop and macro editors). It still lights up on mouse over when it cannot open (that is in
    the library's template).
  - The main window's side rail, `Views/SideRail`, as in Microsoft Store (iNKORE and WinUI have no
    such control; the Store templates its NavigationView): always closed, 72 px wide, two standard
    `ListView`s (pages on top, Settings at the bottom) sharing one selection, so the selection, its
    animated accent pill, hover and arrow keys are the library's. Each item (style `RailItem`:
    `MinWidth` 0, since the library's 88 is wider than the rail, and `Padding` `0,8`): the page's
    Fluent System Icons `_20_Regular` icon (FontSize 20) over its name in caption style; selected,
    the `_20_Filled` icon in `AccentTextFillColorPrimaryBrush` instead, and the name fades out while
    the icon slides down 10 px to the middle (167 ms, curve `0,0,0,1`, WinUI's
    `ControlFastAnimationDuration` and `ControlFastOutSlowInKeySpline`). The update dot on Settings is an
    `AttentionDot` `InfoBadge`. The pages sit on the content layer the NavigationView draws (as in
    Microsoft Store): a Border with `LayerFillColorDefaultBrush`, `CardStrokeColorDefaultBrush` on the
    top and left (`1,1,0,0`) and the top-left corner rounded (`8,0,0,0`). In the rail
    `ListViewItemCompactSelectedBorderThemeThickness` is `4,0,4,4`, so the first page's background starts
    where that layer starts; the Settings list keeps the library's 4 (no extra margin), as Library in Microsoft Store.
  - The main window's Stop all: `AccentButtonStyle` with `AccentButtonBackground`,
    `…PointerOver` and `…Pressed` redefined in its `Button.Resources` from `SystemFillColorCritical`
    (100%, 90%, 80%): red while something runs, the library's disabled grey otherwise.
  - The status dots in the lists are the library's `InfoBadge` dots: Running `SuccessDot`, Disabled
    `CautionDot`, Idle `IdleDotInfoBadgeStyle` (`App.xaml`), the `InformationalDot` in blue instead of
    its grey: `IdleDotBrush`, Windows' default blue per theme (`#60CDFF` dark, `#005FB8` light,
    the highlight color in high contrast), in `ThemeResources.ThemeDictionaries` as the Gallery does.
    Not the accent color: it can be green, like Running.
  - The macro editor's commands (`AppBarButton`s, label under the icon; iNKORE's default is
    `DefaultLabelPosition="Right"`, so set `Bottom`): on the left one `CommandBar` with Add action and
    Record in its `Content`, an `AppBarSeparator`, the selection's commands (Test, Edit, Copy, Paste,
    Duplicate, Group, Delete) and ⋯ (Rename group, Ungroup; Expand all groups, Collapse all groups, each
    on while a group is in the other state, not on the right-click menu, which acts on the clicked rows;
    Shortcuts; `IsDynamicOverflowEnabled="False"`, otherwise it moves commands into ⋯ before the bar is
    full); alone on the right the density
    `ToggleSplitButton` (it acts on the list, not on the actions): click to switch normal / compact,
    its menu Normal / Compact; compact merges the library's `DensityStyles/Compact.xaml` into the
    list, as its Gallery does; normal by default. Shortcuts opens a `ContentDialog` with
    `Views/MacroShortcutsView` (keys as plain `KeyCap`s).
    Opening ⋯ does not light up the bar: `CommandBarBackgroundOpen` and
    `CommandBarBorderBrushOpen` are transparent in its Resources (WinUI joins the open bar and its menu
    into one surface). With the labels under the icons ⋯ makes the bar 74 px tall (its 19 px top padding,
    `CommandBarMoreButtonMargin`) while the buttons are 64 px and their style puts them at the top: so
    the bar sets `VerticalContentAlignment="Center"` and each button `VerticalAlignment="Center"`.
    Add action in `AccentTextFillColorPrimaryBrush`
    (an `AppBarButton` has no accent style), and Record (named "Record actions") with the `Record` icon in
    `SystemFillColorCriticalBrush`. Elsewhere the record dot is a `CriticalDot` `InfoBadge` before the
    text (Record macro, the "Ready to record" title).
  - The macro editor's action list: a thin divider under each row, across the whole row, and hover
    and selection on the whole row, square, so they reach the dividers: in the list's resources
    `ListViewItemCompactSelectedBorderThemeThickness` and `ListViewItemCornerRadius` set to 0. A
    group's open / close arrow is a `Button` with no background, border or hover, in the actions'
    icon column (16 wide), so the group name lines up with the actions.
  - Disabled loops and macros in the overlay (its own fixed dark look, no yellow so they do not stand out):
    a panel row with a dashed outline (`#47FFFFFF`, a `Rectangle` behind the row, as a `Border` cannot be
    dashed) instead of its fill and border, a hollow `#9E9E9E` dot, the name and hotkey in `#8A8A8A` and
    the state "Disabled" in the usual `#C5C5C5`; a floating button with a dashed ring (`#66FFFFFF`, an
    `Ellipse` over the circle) instead of its border, the label and badge in `#8A8A8A` and its background
    at 65% of its opacity. Under Stop all, while the panel can be clicked, the hint "Click: start / stop ·
    Right-click: enable / disable".
  - Specific app (`Views/AppScopeEditor`, in the loop, macro and remap editors): a
    `SwitchSettingsExpander` whose card holds the editable `ComboBox` of running apps, **Pick** (the
    `Eyedropper` icon) and **Browse…**. Pick's overlay (`PickPointWindow` with `pickApp`) outlines the
    window under the cursor with a 2 px `#60CDFF` border and its .exe name on a `#60CDFF` label (fixed
    dark look, like the other overlays). On the loop and macro cards the app follows the status: an
    `AppIconDefault` icon and the .exe name in caption style, secondary color.
  - Sound in the loop and macro editors: the `RadioButtons` with the sounds sit in a standard
    `ScrollViewer` with `MaxHeight="200"`, so the long list scrolls on its own instead of making the
    whole editor scroll. `Views/SoundChoicesScroll` scrolls it to the chosen sound each time it is
    shown.
- **Every place that asks the user for a key or hotkey uses `Views/KeyCaptureField`** (approved by
  the user, made of standard controls), always on one line in three states: nothing set, a "Set
  hotkey" / "Set key" button; waiting, a `ProgressRing` and "Press … (Esc cancels)" (Esc cancels, no
  other button); set, the keys as `KeyCap`s in `AccentFillColorDefaultBrush` with
  `TextOnAccentFillColorPrimaryBrush` text (approved) inside one standard control: where the key can
  be removed (`CanClear`) a `SplitButton` (click: change; its `MenuFlyout`: Change, Clear), otherwise
  a `Button` (click: change). It keeps the same height in every state (a hidden zero-width button
  holding a key sets it), so nothing around it moves. Never build another one (`architecture.md`).
- **A key the user chose is shown as accent `KeyCap`s** wherever it appears: the template
  `AccentKeyCapItem` with the panel `KeyCapRow` (`App.xaml`), used by `KeyCaptureField` and by the list
  cards (a loop's or macro's hotkey after "Toggle with" / "Hold with", a remap's keys in its title).
  The second line of loop and macro cards has `MinHeight="22"`, the height of a line with keys, so
  cards with and without a hotkey are equally tall.
- **The right side of every card in the Loops, Macros and Remap lists is `Views/ItemCardActions`**
  (approved): Play / Stop (in Hold mode its place stays empty; none on remaps), Edit (off while loops or
  macros run), the Enabled switch and More options (off while that loop or macro runs). Edit is
  not in the More options menu. Its
  buttons show filled Fluent System Icons (`Play_16_Filled`, `Stop_16_Filled`, `Edit_16_Filled`,
  `MoreHorizontal_16_Filled`) and are subtle icon buttons.
- **Subtle icon buttons** (approved): an icon-only `Button` that lights up only under the mouse, as
  the `AppBarButton`s in a command bar (iNKORE has no subtle style for a plain `Button`). Merge
  `Views/SubtleButtons.xaml` into the Resources of the element that holds them: it redefines the
  `Button*` background and border resources with `SubtleFillColorTransparent` / `Secondary` /
  `Tertiary` and no border. Used by `ItemCardActions`.
- Something custom is allowed only when no standard component does the job, and **only after
  the user approves that specific case**. Ask first, explain why no standard component fits.
