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
- Every list whose items are not plain text gives its rows an accessible name (`AutomationProperties.Name` in its
  `ItemContainerStyle`, bound to the item's `AccessibleName`, or `PanelAccessibleName` / `RecentAccessibleName` where one
  item is in two lists), never the item's class name that screen readers get
  otherwise: what the row shows, joined by `Views/AccessibleNames.Join` (`Strings.ListSeparator`). Loops and macros: the
  name (in the overlay panel also the state, "Spam, Disabled"); a remap: its note, or its keys.

## Texts

Every text of the UI comes from `Resources/Strings.resx` (`{x:Static r:Strings.Key}`, `Strings.Key`) and is translated
into every language in the same change (`i18n.md`). Layouts must hold longer texts (German, Russian) and right to left
(Arabic): no fixed widths for text, let cards and buttons grow.

- **A group of choices is always the library's `RadioButtons`** (`MaxColumns` for side by side), never `RadioButton`s
  laid out by hand in a `StackPanel` or `WrapPanel`: a `RadioButton` has `MinWidth` 120 and no space after its text,
  so a longer text touches the next one, while `RadioButtons` makes every column as wide as the longest choice with
  its own spacing (7 px).

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
    overlay panel's fixed dark colors, so the chosen opacity can be seen, 36 px high so the row keeps the library's height like the others in the group.
  - Development builds: the orange "Development build" strip under the title bar (fixed colors,
    `build-and-release.md`).
  - Disabled loops, macros and remaps in the lists: the card's icon, header and description use
    `TextFillColorDisabledBrush`, while the card stays enabled (its switch and More options menu must
    keep working; the card's own disabled state would block them).
  - `Views/SwitchSettingsExpander`: a `SettingsExpander` with a switch in its header whose options
    open only while the switch is on (`CanExpand`): otherwise no arrow and the header does not open
    it. Use it for every group whose switch unlocks its options (today: Show in overlay, floating button, Sound
    and Specific app in the loop and macro editors; Settings › Overlay panel, whose switch is Show the overlay panel and
    which opens also while off, `CanExpand` always true, as its Position on screen places the floating buttons too). It still lights up on mouse over when it cannot open (that is in
    the library's template).
  - The main window's side rail, `Views/SideRail`, as in Microsoft Store (iNKORE and WinUI have no
    such control; the Store templates its NavigationView): always closed, 72 px wide, two standard
    `ListView`s (pages on top, Settings at the bottom) sharing one selection, so the selection, its
    animated accent pill, hover and arrow keys are the library's. Each item (style `RailItem`:
    `MinWidth` 0, since the library's 88 is wider than the rail, and `Padding` `0,8`): the page's
    Fluent System Icons `_20_Regular` icon (FontSize 20) over its name in caption style, on one line; a name longer
    than the item shrinks to fit it (a `Viewbox` with `StretchDirection="DownOnly"`, at most 60 px wide: approved by the
    user, as Microsoft Store shrinks "Downloads"), so the rail stays 72 px in every language; selected,
    the `_20_Filled` icon in `AccentTextFillColorPrimaryBrush` instead, and the name fades out while
    the icon slides down by half the name's height and margin, measured (`SideRail.Slide`: a shrunk name is lower), so it
    ends in the middle (167 ms, curve `0,0,0,1`, WinUI's
    `ControlFastAnimationDuration` and `ControlFastOutSlowInKeySpline`). The update dot on Settings is an
    `AttentionDot` `InfoBadge` placed as Microsoft Store's on Downloads (measured, approved): 6 px, 7 px right of the
    icon and 3 px above it, inside the icon's grid so it moves with it. The update bar is the library's `InfoBar`
    with no margins of ours: its action button `HorizontalAlignment` Right (the InfoBar gives the action the rest of
    the row), and while downloading the progress bar ends 46 px from the right, as far as it starts from the left. The pages sit on the content layer the NavigationView draws (as in
    Microsoft Store): a Border with `LayerFillColorDefaultBrush`, `CardStrokeColorDefaultBrush` on the
    top and left (`1,1,0,0`) and the top-left corner rounded (`8,0,0,0`). In the rail
    `ListViewItemCompactSelectedBorderThemeThickness` is `4,0,4,4`, so the first page's background starts
    where that layer starts; the Settings list keeps the library's 4 (no extra margin), as Library in Microsoft Store.
  - The main window's title bar: `TitleBar.ExtendViewIntoTitleBar` with `TitleBar.Height` 48, as Microsoft Store
    and the library's ButtonsOnTitlebar sample: on the left the app icon at 24 px (12 px above and below) and "PuppyMacro"
    in caption style (no version; "DEV" in `TextFillColorSecondaryBrush` in a development build); on the right Enter
    overlay mode (a standard button with the `MapLayers` icon), More options (a standard button with only the `More` icon,
    its name as tooltip; approved design B, the Design canvas "PuppyMacro Import, remap without key, title bar": a `ContextMenu` with **Import…**, the `Download` icon, and
    **Export…**, the `Upload` icon, each with its tooltip) and Stop all, then the window buttons
    (`TitleBar.SystemOverlayRightInset`). Enter overlay mode and Stop all have their hotkey in the tooltip; all of them have
    `WindowChrome.IsHitTestVisibleInChrome` and show on every page. They sit 9 px from the top (the window's 1 px border, then 8), so they look 8 px from the top
    and the bottom, as Microsoft Store's search box (measured). Minimize and close are the library's, as tall as the bar;
    16 px between our last button and them.
  - The main window's Stop all, in that title bar: the filled `Stop_16_Filled` icon and its text, `AccentButtonStyle` with
    `AccentButtonBackground`, `…PointerOver` and `…Pressed` redefined in its `Button.Resources` from
    `SystemFillColorCritical` (100%, 90%, 80%); shown only while something runs, hidden otherwise.
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
    at 65% of its opacity. Under Stop all, while the panel can be clicked, the hint "Click to start or stop ·
    Right-click to enable or disable".
  - Specific app (`Views/AppScopeEditor`, in the loop, macro and remap editors): a
    `SwitchSettingsExpander` whose card holds the editable `ComboBox` of running apps, **Pick** (the
    `Eyedropper` icon) and **Browse…**. Pick's overlay (`PickPointWindow` with `pickApp`) outlines the
    window under the cursor with a 2 px `#60CDFF` border and its .exe name on a `#60CDFF` label (fixed
    dark look, like the other overlays). On the loop and macro cards the app follows the status: an
    `AppIconDefault` icon and the .exe name in caption style, secondary color.
  - The AI page (approved design: the Design canvas "PuppyMacro AI page", second round): the side rail's **AI** item in the
    bottom list above Settings (`BotSparkle_20_Regular` / `_Filled`: the library has no Sparkle alone); the title and a
    description; the "Beta" `InfoBar` (Informational, not closable: the feature is in beta, the user's choice); Allow AI access, a `SwitchSettingsExpander` whose cards are the permissions (Ask before deleting, Ask
    before changing, Ask before changing settings, Start PuppyMacro when an AI app needs it); Apps, one standard
    `SettingsCard` per app built in code (`MainWindow.AppCard`): the app's own icon read from its installed exe at run time
    as an `ImageIcon` (`AiAppTile.ExeIcon`, `SHGetFileInfo`; no logo is shipped, the user approved it) or a `FontIcon`, its
    state with a `SuccessDot` (connected) or `CautionDot` (added, waiting for a restart) `InfoBadge`, and its button
    (Connect with `AccentButtonStyle`, Remove and Copy configuration with the standard style: never `Style = null`, which
    drops the implicit style) or a `HyperlinkButton` to get it; Recent activity, a card with the last 4 requests, a
    Restore `HyperlinkButton` on what an AI app changed or deleted, and **View all** next to its title.
    `AiActivityWindow`: the filters as standard `ComboBox`es and `TextBox` with headers, the `ListView` grouped by day,
    the selected request's details in a card below with Restore.
  - The status bar (approved): a 28 px row at the bottom of the main window, a 1 px `CardStrokeColorDefaultBrush` line on
    top, shown only while an AI app is connected: for each one a green dot (`SystemFillColorSuccessBrush`) whose halo grows
    and fades in 2.4 s, forever (still when `SystemParameters.ClientAreaAnimation` is off), and its name in caption style in
    the same green. A click opens the AI page.
  - The windows of an AI app's request (`AiRequestWindow`, `PickPointWindow`, an editor opened by `open_item`) open
    centered on the screen and topmost until the user activates them (`Views/AiWindow.BringToFront`): the AI app is in
    front and Windows may not let PuppyMacro take the focus. `AiRequestWindow` (title "AI request - app") shows "Asked by
    …" in caption style with the `BotSparkle` icon, a key request also the AI's description and, under "Instructions"
    (`BodyStrongTextBlockStyle`), its steps as "1. …". An editor opened by `open_item` shows the AI's title and
    description in its `NoticeBar`. `AiReadyWindow`, before a pick or a placement, is drawn over other apps like the
    overlays (fixed dark look, borderless, topmost, centered on the screen; `WS_EX_NOACTIVATE`, so a click or a drag never
    takes the focus from the app in front: dragged by hand with the mouse captured, not `DragMove`): "app
    asks", the title at 20 px semibold, the description, "Instructions" and the steps, numbered in `#60CDFF`, one per line,
    then "Press Enter when you're ready · Esc cancels" with keycaps and Start (a button template of its own in the accent
    blue, like the recording bar's). `PickPointWindow` with several points shows
    "2 / 3" before its text and a numbered `#60CDFF` circle where each point was clicked; for an AI app, the current point's
    label (the AI's text) in a see-through `#60CDFF` box 20 px below and right of the cursor, following it (on the other
    side near the screen's edges), never hit-tested.
  - The status bar's apps sit on the right; it is inside the pages' content layer, so the side rail runs the whole height.
  - An editor's History view (approved design): the third item of its Form / Code switch, shown only for a saved item; the
    versions in a standard `ListView` grouped by day (time, who, summary, "(current)" on the newest), the chosen one's
    changes in a card on the right with **Restore this version** (`AccentButtonStyle`); the editor's `NoticeBar`, a standard
    `InfoBar`, says that the restored version waits for Save.
  - Export's choices (approved design: the Design canvas "PuppyMacro Export dialog", option B): a standard `ContentDialog`
    "Export" with `Views/ExportOptionsView`: `RadioButtons` Everything (its description and the total size under it) and
    Choose what to export; under them, indented 28 px (the radio button's circle and its gap, so the boxes line up with
    the labels), a `CheckBox` per part with its description and size in caption style, enabled only with Choose; Loops,
    macros, remaps and settings always checked and disabled. Export… is the default button, then the file dialog.
  - Import and Export of loops, macros and remaps (approved design: the Design canvas "PuppyMacro Import and Export", A1 to
    A3): the title bar's More options › Import… and Export… open a standard `ContentDialog` each, with `Views/ImportItemsView` /
    `Views/ExportItemsView`: the items in an `ItemsControl` grouped by kind (Loops, Macros, Remaps: a
    `BodyStrongTextBlockStyle` header per group) inside a bordered card (`CardStrokeColorDefaultBrush`,
    `CardBackgroundFillColorDefaultBrush`, 4 px corners) that scrolls, one `CheckBox` per item whose content is its name, the
    card's summary in caption style and its hotkey as accent `KeyCap`s. Import: the file (the `Page` icon, its name, "From
    PuppyMacro 1.17.0 · 2 loops, 1 macro" in caption style) and **Choose another file…**; a three-state **Select all**; under
    an item, what changes on import in caption style in `SystemFillColorCautionBrush` and why it can't be imported in
    `SystemFillColorCriticalBrush` (its box disabled); the `InfoBar` "Imported items start disabled." (Informational, not
    closable); the button **Import N items**, or **Check for updates** for a file of a newer version. Export: a description,
    Search (`TextBox`) and the kind (`ComboBox`: All kinds, Loops, Macros, Remaps), a three-state **Select all shown** with
    "N shown · M selected" on the right, the button **Export N items…**, then the file dialog. Each card's More options menu
    starts with **Export…** (the `Upload` icon, the opposite of Import's `Download`; for remaps after Duplicate), which opens
    the file dialog at once. Both dialogs are 800 x 900 px (less when the window is smaller; approved, the Design canvas "PuppyMacro Import, remap without key, title bar"), through the
    library's `ContentDialogMin/MaxWidth` and `…Height` resources set on the dialog (`Views/DialogFill`): the dialog shows its
    content in a ScrollViewer, so `DialogFill` gives the view the height shown and the list's row is `*`, taking the free
    space. 32 px above **Select all** / **Select all shown** (after the file or the search), 8 below; Import's `InfoBar` at
    the bottom, 16 px under the list. Import's notes are short natural sentences (`i18n.md`): "…, so this comes in without a
    hotkey.", "You already have a loop with this name, so this one is called "Farm (2)"."
  - A remap without a hotkey (only after an import, `data.md`; approved design B, the Design canvas "PuppyMacro Import, remap without key, title bar"): its card shows "No hotkey yet"
    instead of the source keys, its second line is the `Warning` icon and "Choose a hotkey with Edit to turn it on." in
    `SystemFillColorCautionBrush` instead of the note, and its switch is off and disabled (`ItemCardActions.CanSwitch`).
  - A file dragged over the main window (approved, the Design canvas "PuppyMacro Import, remap without key, title bar"; custom: no standard component covers a window): `DropOverlay`,
    over the whole window, title bar too, with `Panel.ZIndex` 100 and no hit testing. The window's content is blurred
    (`BlurEffect`, radius 6) and covered by `SolidBackgroundFillColorBaseBrush` at 82%; a dashed `Rectangle` (2 px, `4 3`,
    8 px corners) 12 px inside the edges; in the middle a 56 px icon, a `TitleTextBlockStyle` title and the file's name in
    caption style. A `.puppyitems` file: the line in `AccentFillColorDefaultBrush`, `ArrowDownload_48_Regular` in the accent
    text color, "Drop to import". A backup: the line and `ArchiveSettings_24_Regular` in `TextFillColorSecondaryBrush`,
    "Backups open from Settings" and why, and the drop is refused. Any other file: nothing, as before.
  - A backup opened from File Explorer (approved by the user as described): `BackupFileWindow`, a small window alone (440 px,
    as `GroupNameWindow`: Mica, its buttons' area at the bottom): the `ArchiveSettings_24_Regular` icon at 32 px in the
    secondary text color, "Backups open from Settings" in `SubtitleTextBlockStyle`, why (it replaces all the data) and where
    (Settings › Backup, with the page's own names), the file's name in caption style, and only **Close** (`AccentButtonStyle`,
    Enter and Esc).
  - File icons in File Explorer (approved: the same canvas, Icons B), drawn by `scripts/file-icons.ps1`
    (`Assets/FileIcons`): `.puppyitems`, a white page with a folded corner and the paw in `#0F6CBD`, centered; `.puppymacro`
    (backup), a dark archive box with the paw in `#60CDFF`.
  - Sound in the loop and macro editors: the `RadioButtons` with the sounds sit in a standard
    `ScrollViewer` with `MaxHeight="200"`, so the long list scrolls on its own instead of making the
    whole editor scroll. `Views/SoundChoicesScroll` scrolls it to the chosen sound each time it is
    shown.
- **Every place that asks the user for a key or hotkey uses `Views/KeyCaptureField`** (approved by
  the user, made of standard controls), always on one line in three states: nothing set, a "Set
  hotkey" / "Set key" button with the `Bullseye` icon before its text; waiting, a `ProgressRing` and "Press … (Esc cancels)" (Esc cancels, no
  other button); set, the keys as `KeyCap`s in `AccentFillColorDefaultBrush` with
  `TextOnAccentFillColorPrimaryBrush` text (approved) inside a `SplitButton`, everywhere (click: change;
  its `MenuFlyout`: Change, Clear only where the key can be removed (`CanClear`), then for each Ctrl, Alt,
  Shift and Win of the value a `Separator` and three checkable `MenuItem`s, "Left Ctrl", "Right Ctrl",
  "Left or right Ctrl", built in code; iNKORE has no radio menu item). It keeps the same height in every state (a hidden zero-width button
  holding a key sets it), so nothing around it moves. Never build another one (`architecture.md`).
- **An action is edited in `Views/ActionBox`** (approved), the same in a loop editor row and in the macro's
  action window: a `SettingsExpander` with the type's icon (one `FontIcon` per kind), its name, a summary and
  its main field in the header (key, `MouseButtonBox`, direction, or `PositionPicker` for Move to); opened,
  one `SettingsCard` per option. Loop: While the loop runs, Every, Hold for, Mode, Times, Pause between
  (off with Times 1), Position (a `ToggleSwitch`: off, where the pointer is; on, a point); with Hold down
  only Position stays. Macro: Position, Mode, Hold for, Times, Pause between; Name and Delay are cards
  above the box in the window. Key down and Key up have no options: a plain `SettingsCard` with no arrow.
  A point is always `Views/PositionPicker` (on one line: "X" and "Y" each before its `NumberBox`, as "ms" after a
  number, so the card keeps its height; Pick on screen); a new macro Press key / Key down / Key up waits for its key at once; a
  mouse button always `Views/MouseButtonBox`: the list and, next to it, Detect (an icon button) that waits for a mouse
  button through `LoopEngine.BeginCapture` (only the ring and "Press a mouse button (Esc cancels)" show meanwhile, the list
  hidden too, as the key field) and selects it; a key instead is rejected ("Use Press key for keys.").
- **Icons of the buttons that wait, pick or move** (approved; every such button has one): a button that waits for
  a key or a mouse button, or picks something on the screen, has `Bullseye` (Set hotkey / Set key and Change in
  `KeyCaptureField`; `PlaceButton`: Pick on screen; Specific app's Pick; Detect in `MouseButtonBox`), as the user
  chose; one that moves the overlay has `Move` (`PlaceButton` with `MovesOverlay`: a floating button's Position,
  Settings' Position on screen). `Views/PlaceButton` is the one button for picking and moving (icon, then its text).
- **Every slider is `Views/ValueSlider`**: its value as text, then a 200 px standard `Slider`, on one line on the right of
  its card, as in Windows Settings (Settings › Overlay panel › Opacity, Settings › Sound, a floating button's Opacity, a
  macro's Speed). A percentage in steps of 5 by default; `Step` and `Format` change it (Speed: ×0.25 to ×4). Never a card
  with `ContentAlignment="Vertical"` for a slider.
- The loop editor's bar (approved): a `CommandBar` above the actions with one `AppBarButton` per action
  (Press key, Click, Paste text), label under the icon as the macro editor's bar, no overflow button.
- The editors (loop, macro, remap) have an **Enabled** card with a `ToggleSwitch`, first on the right; the
  same value as the list card's switch. Their titles name what they edit: "Edit loop - Spam", "Edit
  click - Open inventory" (its name, or its description), "Edit remap - Sample: Caps Lock to Esc" (its
  note, or its keys). The macro action's Test button shows the filled Play icon before its text.
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
