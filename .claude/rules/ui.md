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
- Every list whose items are not plain text gives its rows an accessible name, never the item's class name that screen
  readers get otherwise: what the row shows, joined by `Views/AccessibleNames.Join` (`Strings.ListSeparator`).

## Texts

Every text of the UI comes from `Resources/Strings.resx` (`{x:Static r:Strings.Key}`, `Strings.Key`) and is translated
into every language in the same change (`i18n.md`). Layouts must hold longer texts (German, Russian) and right to left
(Arabic): no fixed widths for text, let cards and buttons grow.

- **A group of choices is always the library's `RadioButtons`** (`MaxColumns` for side by side), never `RadioButton`s
  laid out by hand: a lone `RadioButton` leaves no space after its text, so a longer text touches the next one, while
  `RadioButtons` makes every column as wide as the longest choice with its own spacing.

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
- The windows drawn over other apps are transparent, borderless and topmost, with a fixed dark look of their own in
  every theme: they sit on top of fullscreen apps.
- Use the library's controls with their default styles (`AccentButtonStyle` for the main action), `FontIcon` with
  `SegoeFluentIcons` (or `FluentSystemIcons` where a Regular / Filled pair is needed), the text styles and the theme
  brushes.
- Lay out pages as the library's Gallery and Windows Settings do (cards in a stack, a `BodyStrongTextBlockStyle` title
  above each section). Take sizes and spacing from there, never invent them.
- **No custom control templates, no restyled controls, no own colors or sizes for standard
  controls, no hand cursors** (Windows shows what can be clicked by lighting it up, not with
  the cursor).
- Known library bug (0.10.2, rewritten in its `main` in January 2026, not released yet): a
  `FontIcon` whose `Icon` changes after it is shown (style trigger, binding) can set its own `Icon`
  to null as a local value (`FontIconSource.UpdateIconData` compares the font family by
  reference), and from then on it never changes again. So **never change a `FontIcon`'s `Icon`**:
  put one `FontIcon` per icon and switch their `Visibility`.
- Known library bug (0.10.2, still in its `main`): `SettingsCard` gives a `NumberBox` inside it
  `{StaticResource SettingsCardContentMinWidth}`, which is only in the theme dictionaries, so the
  layout throws "UnsetValue is not a valid value for property MinWidth" (a theme style cannot see
  the app's resources). So **every `NumberBox` inside a `SettingsCard` sets
  `MinWidth="{StaticResource SettingsCardContentMinWidth}"`** (defined in `App.xaml` with the
  library's value): a local value wins over that style. The unit test
  `ControlResourcesTests` checks every XAML file for it; when updating the library, check whether
  the bug is gone.
- Approved by the user (custom on purpose):
  - Drag to move: an accent line where the dragged items go (above or below a list card, beside a tile in the Grid
    view, a border on a macro editor row). In a grouped view an item moves only inside its group; the Table view has no
    drag (More options › Move up / Move down).
  - Settings > Overlay > Opacity: a small preview of the panel with the overlay panel's fixed dark colors, so the chosen
    opacity can be seen, keeping the library's row height like the others in the group.
  - Development builds: the orange "Development build" strip under the title bar (fixed colors,
    `build-and-release.md`).
  - Disabled loops, macros and remaps in the lists: the item's texts and icon in the disabled text color, while the card
    stays enabled (its switch and More options menu must keep working; the card's own disabled state would block them).
  - **The views of the Loops, Macros and Remap pages** (approved: the Design canvas "PuppyMacro lists redesign", B + C;
    `MainWindow.xaml`, `MainWindow.Lists.cs`, `Views/PageViews`): on the search's row, the filter first (on the left, the
    user's choice), the search box, then three standard `ToggleButton`s, **List**, **Table** and **Grid** (an icon, its
    name as tooltip; the one in use stays checked). Each page keeps its own view in the settings (`data.md`); at first
    Loops List, Macros Table, Remap List (the user's choice).
    - **List**: one clickable `SettingsCard` per item, on one line, the library's height.
    - **Grid**: tiles, each a clickable `SettingsCard` (background, border, hover and click stay the library's), every
      tile of a page as wide and as tall; at least 320 px wide (bigger tiles, the user's choice), as many columns as fit.
    - List and Grid group the items by the app they work in, All apps first, then the apps by name, each in the user's
      order; only when the page has items for more than one app. No line under the group headers (the library's
      `GroupItem` draws one: not in the approved design; the user's choice).
    - **Table**: the library's `ListView` with a `GridView`, not grouped, in a card. **Hotkey** is the second column (the
      user's choice). A click never selects a row. Columns fit their content in every language (measured); no sideways
      scrolling: when the window narrows, What it does and then Works in are hidden.
    - **A click on the item opens its editor** (in the table also Enter), or More options › Edit; there is no Edit
      button. Loops and macros open only while nothing runs. Keys never get trimmed: texts give up the space.
  - `Views/SwitchSettingsExpander`: a `SettingsExpander` with a switch in its header whose options
    open only while the switch is on (`CanExpand`). Use it for every group whose switch unlocks its options
    (Settings › Overlay panel opens also while off: its Position on screen places the floating buttons too). It still
    lights up on mouse over when it cannot open (that is in the library's template).
  - The main window's side rail, `Views/SideRail`, as in Microsoft Store (iNKORE and WinUI have no
    such control; the Store templates its NavigationView): always closed, two standard `ListView`s (pages on top,
    Settings at the bottom) sharing one selection, so the selection, its accent pill, hover and arrow keys are the
    library's. Each item: the page's Fluent System Icons Regular icon over its name; selected, the Filled icon in the
    accent color, and the name fades out while the icon slides to the middle (WinUI's fast animation). A name longer
    than the item shrinks to fit it (approved by the user, as Microsoft Store shrinks "Downloads"), so the rail stays
    72 px wide in every language. The update dot on Settings is placed as Microsoft Store's on Downloads (measured,
    approved). The update bar is the library's `InfoBar` with no margins of ours. The pages sit on the content layer the
    NavigationView draws (as in Microsoft Store).
  - The main window's title bar, extended into the window as in Microsoft Store and the library's ButtonsOnTitlebar
    sample: on the left the app icon, "PuppyMacro" (no version; "DEV" in a development build) and More options (a
    subtle icon button, its background only under the mouse, the user's choice; placed there by the user, the Design
    canvas "PuppyMacro title bar, Import, backup window"; approved design B, the Design canvas "PuppyMacro Import, remap
    without key, title bar": a menu with **Import** and **Export**, no "…": the user's choice); on the right Enter
    overlay mode and Stop all, then the window buttons. They show on every page, with their hotkey in the tooltip, and
    sit as far from the top and the bottom as Microsoft Store's search box (measured).
  - The main window's Stop all: an accent button in the critical red, shown only while something runs.
  - The status dots in the lists are the library's `InfoBadge` dots; Idle is Windows' default blue per theme, not the
    accent color: it can be green, like Running.
  - The macro editor's commands (`AppBarButton`s, label under the icon; iNKORE's default is
    `DefaultLabelPosition="Right"`, so set `Bottom`): on the left one `CommandBar` with Add action, Add condition,
    Component and Record in its `Content` (the bar's header: always shown), then the selection's commands, on the bar
    while they fit and moved into ⋯ from the last one when the window narrows (the library's dynamic overflow, the
    user's choice: every button is 68 px wide with its label under it, so at the default width the last ones may already
    not fit), and the secondary commands, always in ⋯. Expand all and Collapse all groups are in ⋯, not on the right-click menu, which acts on
    the clicked rows. Alone on the right the density `ToggleSplitButton` (it acts on the list, not on the actions);
    compact merges the library's `DensityStyles/Compact.xaml` into the list, as its Gallery does; normal by default.
    Opening ⋯ does not light up the bar (its `…Open` brushes are transparent: WinUI joins the open bar and its menu into
    one surface). With the labels under the icons ⋯ makes the bar taller than the buttons, which their style puts at
    the top: so the bar and each button are centered vertically. Add action in the accent text color (an `AppBarButton`
    has no accent style), and Record with the `Record` icon in the critical red. Elsewhere the record dot is a
    `CriticalDot` `InfoBadge` before the text. **Record macro** on the Macros page shows instead the editor's `Record`
    icon before its text (approved: the user found the dot too small).
  - The macro editor's action list: a thin divider under each row, across the whole row, and hover and selection on the
    whole row, square, so they reach the dividers. A group's open / close arrow is a `Button` with no background,
    border or hover, in the actions' icon column, so the group name lines up with the actions.
  - Disabled loops and macros in the overlay (its own fixed dark look, no yellow so they do not stand out): a panel row
    with a dashed outline instead of its fill and border, a floating button with a dashed ring instead of its border,
    their texts greyed.
  - The overlay panel's layout (approved: the Design canvas "Overlay panel redesign", A): under the title the click
    hints (while it has rows and can be clicked), the rows, a line, **Stop all** (while there is something to stop),
    **Open PuppyMacro** under it, styled as Stop all. Without rows: only a hint, the line and Open PuppyMacro.
  - Specific app (`Views/AppScopeEditor`, in the loop, macro and remap editors): a `SwitchSettingsExpander` with the
    editable `ComboBox` of running apps, **Pick** and **Browse…**. Pick's overlay outlines the window under the cursor
    with its .exe name (fixed dark look, like the other overlays). On the loop and macro cards the app follows the
    status.
  - The AI page (approved design: the Design canvas "PuppyMacro AI page", second round): the side rail's **AI** item in
    the bottom list above Settings (`BotSparkle`: the library has no Sparkle alone); the "Beta" `InfoBar`
    (Informational, not closable: the feature is in beta, the user's choice); Allow AI access, a
    `SwitchSettingsExpander` whose cards are the permissions; Apps, one standard `SettingsCard` per app with the app's
    own icon read from its installed exe at run time (no logo is shipped, the user approved it), its state and its
    button (standard styles: never `Style = null`, which drops the implicit style); Recent activity with **View all**.
    `AiActivityWindow`: standard filters, the requests grouped by day, the selected one's details below with Restore.
  - The status bar (approved): a row at the bottom of the main window, inside the pages' content layer (the side rail
    runs the whole height), shown only while an AI app is connected: on the right, for each one a pulsing green dot and
    its name. A click opens the AI page.
  - The windows of an AI app's request (`AiRequestWindow`, `PickPointWindow`, an editor opened by `open_item`) open
    centered on the screen and topmost until the user activates them (`Views/AiWindow.BringToFront`): the AI app is in
    front and Windows may not let PuppyMacro take the focus. `AiReadyWindow`, before a pick or a placement, is drawn
    over other apps like the overlays, with `WS_EX_NOACTIVATE`, so a click or a drag never takes the focus from the app
    in front (dragged by hand with the mouse captured, not `DragMove`); its Start button has a template of its own, like
    the recording bar's. For an AI app, `PickPointWindow` shows the current point's label next to the cursor, never
    hit-tested.
  - An editor's History view (approved design): the third item of its Form / Code switch, shown only for a saved item;
    the versions grouped by day, the chosen one's changes on the right with **Restore this version**; the editor's
    `NoticeBar` says that the restored version waits for Save.
  - Export's choices (approved design: the Design canvas "PuppyMacro Export dialog", option B): a standard
    `ContentDialog` with `Views/ExportOptionsView`: Everything or Choose what to export, then a `CheckBox` per part,
    lined up with the labels, enabled only with Choose; loops, macros, remaps and settings are always exported.
  - Import and Export of loops, macros and remaps (approved design: the Design canvas "PuppyMacro Import and Export", A1
    to A3): a window each (`ItemsWindow`, `Views/ImportItemsView` / `Views/ExportItemsView`), the items grouped by kind
    in a card that scrolls, one `CheckBox` each. Imported items start disabled (the Import window says it). Each card's
    More options menu has **Export**. The windows open as the editors, not as a `ContentDialog` (the user's choice: a
    `ContentDialog` covered the main window's title bar, under its window buttons, and could not grow past the main
    window); the main window is hidden meanwhile. Import's notes are short natural sentences (`i18n.md`).
  - A remap without a hotkey (only after an import, `data.md`; approved design B, the Design canvas "PuppyMacro Import,
    remap without key, title bar"): in every view a warning, "No hotkey yet", instead of the source keys, and its switch
    is off and disabled.
  - A file dragged over the main window (approved, the Design canvas "PuppyMacro Import, remap without key, title bar";
    custom: no standard component covers a window): `DropOverlay`, over the whole window, title bar too, its content
    blurred. A `.puppyitems` file: "Drop to import". A backup: "Restore this backup from Settings" and why, and the drop
    is refused. Any other file: nothing.
  - A backup opened from File Explorer (approved by the user as described): `BackupFileWindow`, a small window alone,
    as `GroupNameWindow`: its title "Restore this backup from Settings" (the user chose it, the Design canvas
    "PuppyMacro title bar, Import, backup window", 3B), what restoring replaces and where, the file's name; **Open
    Settings** (PuppyMacro on Settings › Backup) and **Close**.
  - File icons in File Explorer (approved: the same canvas, Icons B), drawn by `scripts/file-icons.ps1`.
  - Sound in the loop and macro editors: the sounds' `RadioButtons` sit in a standard `ScrollViewer` with a maximum
    height, so the long list scrolls on its own instead of making the whole editor scroll. `Views/SoundChoicesScroll`
    scrolls it to the chosen sound each time it is shown.
  - Conditions on the screen (approved: the Design canvas "Macro Conditions", option B, made with the current UI's
    controls): in the macro editor an If is a row with **Then** and **Else** rows under it, their actions indented one
    step more; **+ Action** and **+ Condition** on the left of the bar, the same + icon and a short name under it (the
    user's choice; the tooltips say Add action and Add condition). The condition window is the action window with the conditions as
    `SettingsExpander`s and a card that says what PuppyMacro sees now; the text recognition note is an `InfoBar` above the
    cards. `ScreenPickWindow` (pick a pixel with the enlarged pixels around the pointer, take an image, draw an area) is
    drawn over a still picture of the screen, with the overlays' fixed dark look.
  - The Library (approved: the Design canvas "PuppyMacro Library", dark): **Component** in the macro editor's bar after
    + Condition (the user's choice: the component's Link icon, as its linked rows, in the theme's success green
    `SystemFillColorSuccessBrush`; Add component in its tooltip); a linked row is one line, greyed, with a link icon, its
    component's name and "Component · collection · actions", no delay; a click (Edit, double-click) opens the component
    read only (`ComponentWindow`: Unlink this row, Open in Library, Close), never the action window; Save as component
    and Unlink in ⋯ and the right-click menu. One Library view in two modes (`LibraryWindow`): pick (Add component: check
    boxes, Add linked) and manage (the **Library** button on the Macros page: Edit details, History, Delete, Import,
    Export collection); collections on the left, the components of the chosen one on the right, the selected one showing
    what it plays. A refused import says why in the Import window. Delete, and Delete collection in the collection's ⋯, are
    disabled while something uses them, with why in their tooltip (`ToolTipService.ShowOnDisabled`; the user's choice).
  - Components as functions (approved: the Design canvas "PuppyMacro Component functions", with the app's existing
    controls, the user's choice): **Edit** in the Library opens the component in the macro editor, its right column
    replaced by What it does, Parameters (a card each, Add parameter) and Used as a condition (Can be used as a condition,
    What it finds); `ParameterWindow`; a field that can take a parameter's value has a list before it, "A value" or "From
    …", as the If's Wait card, and shows its own control only with "A value" (`Views/SlotBindings`); a click's Position lists
    the Point parameters with Fixed point and the found places; a linked row shows its values and its window has
    Arguments (`Views/ArgumentsEditor`, a card per parameter, the same list pattern, `Views/ValueEditor` with the app's own
    controls for each type) with Save and Cancel; Add condition › Condition from component (the user's name, with the component's Link icon); `SignatureUpdateWindow`
    before a save that changes the calls.
  - Grid parameters (approved: the same canvas, boards 1-4, drawn on the real windows): Grid in the parameter window's
    Type list; a grid's value is a `SettingsExpander` (`Views/GridValueEditor`: first and last cell with Pick on screen,
    columns, rows, how many cells, **Show the cells**) wherever a value is edited (Example, Default, an argument given as a
    value, Update the calls); Position lists "A cell of …" for each Grid parameter, then the **Cell** card with the same
    list as the other fields; a pixel's point the same. Show the cells is `ScreenPickWindow` in its Cells mode: rings and
    numbers over the still picture, closed by a click or Esc.
- **Every place that asks the user for a key or hotkey uses `Views/KeyCaptureField`** (approved by
  the user, made of standard controls), always on one line in three states: nothing set, a "Set hotkey" / "Set key"
  button; waiting, a `ProgressRing` and "Press … (Esc cancels)" (Esc cancels, no other button); set, the keys as accent
  key caps inside a `SplitButton` (click: change; its menu holds the other choices). It keeps the same height in every
  state, so nothing around it moves. Never build another one (`architecture.md`).
- **An action is edited in `Views/ActionBox`** (approved), the same in a loop editor row and in the macro's action
  window: a `SettingsExpander` with the action's main field in its header and one `SettingsCard` per option. Key down and
  Key up have no options: a plain `SettingsCard` with no arrow. A point is always `Views/PositionPicker`; a mouse button
  always `Views/MouseButtonBox`, whose Detect waits for a mouse button as the key field waits for a key.
- **Icons of the buttons that wait, pick or move** (approved; every such button has one): a button that waits for a key
  or a mouse button, or picks something on the screen, has `Bullseye`, as the user chose; one that moves the overlay has
  `Move`. `Views/PlaceButton` is the one button for picking and moving (icon, then its text).
- **Every slider is `Views/ValueSlider`**: its value as text, then a standard `Slider`, on one line on the right of its
  card, as in Windows Settings. Never a card with `ContentAlignment="Vertical"` for a slider.
- The loop editor's bar (approved): a `CommandBar` above the actions with one `AppBarButton` per action, label under
  the icon as the macro editor's bar, no overflow button.
- The editors (loop, macro, remap) have an **Enabled** card with a `ToggleSwitch`, first on the right; the
  same value as the list card's switch. Their titles name what they edit: "Edit loop - Spam".
- **Keys are always `Views/KeyCaps`** (approved). **A key the user chose is shown in the accent color**
  (`KeyCaptureField`, Import and Export). **The Loops, Macros and Remap lists** (every view) show plain `Small` key
  caps, greyed with a disabled item (the user's choice). The Shortcuts dialog and the record prompt still write
  `Border`s with the `KeyCap` style.
- **The buttons of every item in the Loops, Macros and Remap lists, in each view, are `Views/ItemCardActions`**
  (approved): Play / Stop (none on remaps), More options, then the Enabled switch, last, without the library's On / Off
  text (English only, not in the approved design). There is no Edit button: a click on the item opens its editor. More
  options is the same menu on the three pages, **Edit** first.
- **Subtle icon buttons** (approved): an icon-only `Button` that lights up only under the mouse, as
  the `AppBarButton`s in a command bar (iNKORE has no subtle style for a plain `Button`). Merge
  `Views/SubtleButtons.xaml` into the Resources of the element that holds them.
- Something custom is allowed only when no standard component does the job, and **only after
  the user approves that specific case**. Ask first, explain why no standard component fits.
