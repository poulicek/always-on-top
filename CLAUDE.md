# Always on Top – notes for Claude

Tray-only Windows app: adds a checkable "Always on Top" item to the system menu (title-bar right-click / Alt+Space)
of every top-level window, pins/unpins with `HWND_TOPMOST`, and draws an accent-coloured border around pinned windows.
Public on GitHub (`poulicek/always-on-top`, MIT). Machine-specific notes can go into `CLAUDE.local.md` (git-ignored).
Keep everything committed purely about this project – no personal data, no names, no local paths.

When working from a packaged (MSIX) host such as the Claude desktop app, writes to `%LOCALAPPDATA%`/`%APPDATA%` are
redirected into the package's LocalCache – use the `\\localhost\c$\...` admin-share path and launch the app via
`explorer.exe` (see the guard in `Publish.ps1`).

## Stack and layout

- .NET 10 WinForms (+ WPF for UI Automation), TFM `net10.0-windows10.0.17763.0`, single-file framework-dependent
  win-x64 publish. Assembly name `Always on Top` (space on purpose: Task Manager name).
- `Program.cs` – single instance (mutex + process count), `--render-tray <dir>`, `--after-update <pid>`, forwards
  other commands (`Ipc.cs`) to the running instance.
- `TrayApp.cs` – ApplicationContext: NotifyIcon, native menu, settings, autostart, theme events.
- `SystemMenuHook.cs` – inserts the item into other windows' system menus and detects when it is chosen.
- `EdgeSnapper.cs` – magnetic screen edges while dragging any window; `Edges` (frame, scale, padded work area,
  corner dock test). `Memory.cs` – GC + working-set trim after start-up and after the tray menu.
- `HoverReveal.cs` – hover a covered window's title bar → it shows above the pinned window until the cursor leaves.
- `TaskbarSplitter.cs` – optional: per-window AppUserModelID so each window gets its own taskbar button.
- `EdgeAppMode.cs` – an Edge window docked in a corner is replaced by a compact Edge app window.
- `Updater.cs` – self-update from GitHub Releases.
- `Pinner.cs` – pinned set, pin/unpin, 2 s sweep, borders on/off. `PinBorder.cs` – the border overlay window.
- `TrayIcons.cs` (Segoe Fluent Icons glyphs, PNG-ICO wrap, preview sheet), `NativeMenu.cs` (Win32 popup menu),
  `Theme.cs` (taskbar/app theme, accent, dark menus, tray icon size), `Native.cs` (P/Invoke),
  `Settings.cs` (Paths, Log, Settings json), `Autostart.cs`, `HostForm.cs`.
- `installer/AlwaysOnTop.iss` (Inno Setup), `.github/workflows/release.yml`, `tools/Make-Icon.ps1` (app.ico),
  `Publish.ps1` (local build + install). Installed to `%LOCALAPPDATA%\Always on Top\` (exe, `settings.json`,
  `log.txt`).

## Workflow

- Build: `dotnet build -c Release`. Local install: `powershell -ExecutionPolicy Bypass -File Publish.ps1 -Launch
  [-Version x.y.z]` (asks the running app to `--exit`, publishes to `bin\publish`, copies via `\\localhost\c$`,
  launches via Explorer). A local build is replaced by the next promoted release with a higher version.
- Release: bump `<Version>` in the csproj, push, `git tag vX.Y.Z` + `git push origin vX.Y.Z`. CI publishes a
  PRE-release with `AlwaysOnTop-Setup.exe`, `AlwaysOnTop-win-x64.zip` and `.sha256` files; promote it to Latest on
  GitHub to ship it. Every push to `main` builds the same files as a workflow artifact.
- Drive the running app with a second instance: `"...\Always on Top.exe" --menu|--exit|--status` (`--status` writes
  pinned windows, settings and working set to the log). Dev commands: `--pin <hwnd>`, `--unpin <hwnd>`,
  `--pin-corner <hwnd>` (pins like a corner dock), `--split-on`, `--split-off` (decimal window handles).
- Log: `%LOCALAPPDATA%\Always on Top\log.txt`.
- **Test safety**: never touch windows the tests did not start. Do not take a test window from
  `GetForegroundWindow()` – Windows does not always hand the foreground to a new process, and the foreground window
  may belong to a real app. Find test windows by the started process id (EnumWindows + pid; prefer the foreground
  window when it belongs to that pid – Character Map has a second top-level window – else require
  WS_CAPTION|WS_SYSMENU), abort if not found, only send keystrokes after verifying the foreground window is the test
  window, check `WindowFromPoint` before any mouse press, keep test windows away from other topmost windows, and close
  leftovers (`Get-Process charmap | Stop-Process`). Prefer the dev commands over synthetic input.
- Functional test pattern: start `charmap.exe` (classic Win32 window), `keybd_event` Alt+Space then `T`, check
  `WS_EX_TOPMOST`; mouse path: `GetMenuItemRect` of id `0x7A10` + `SetCursorPos` + `mouse_event`. Test scripts must
  call `SetProcessDpiAwarenessContext(-4)` on a scaled screen. In PowerShell P/Invoke pass `[NullString]::Value`, not
  `$null`, for a null string (`$null` becomes `""`).

## Design decisions (and why)

- **No DLL injection.** The item is inserted cross-process (`GetSystemMenu(hwnd, FALSE)` + `InsertMenuItem`; menu
  handles are global). The resulting `WM_SYSCOMMAND` goes to the target's wndproc, which we cannot see, so the choice
  is detected while the menu is open: `EVENT_SYSTEM_MENUPOPUPSTART` → `MN_GETHMENU` identifies our menu; a
  low-level mouse hook (`MenuItemFromPoint` on button-up), a low-level keyboard hook (Enter/Space with our item
  `MF_HILITE`, or `T`). The target ignores the unknown command id. Ids: item `0x7A10`, separator `0x7A20`
  (< 0xF000, low 4 bits zero). The item goes just above *Close* with its own separator.
- **When to insert**: existing windows at start (EnumWindows), `EVENT_SYSTEM_FOREGROUND` plus a 400 ms retry
  (dialogs are activated before they are visible), `EVENT_SYSTEM_MENUSTART` (late but still in time for the popup).
- **Per-monitor DPI aware** (manifest via `ApplicationHighDpiMode`) – LL hook coordinates are physical; a
  DPI-unaware process would get `MenuItemFromPoint` wrong.
- **WinUI/UWP limitation**: `GetSystemMenu` on Settings / Snipping Tool returns a handle that fails with
  `ERROR_INVALID_MENU_HANDLE` (1401) for other processes – they draw their own menu. Accepted.
- **Border** (`PinBorder`): `DWMWA_BORDER_COLOR` only works on own windows, so it is a separate layered window
  (`WS_EX_LAYERED|TRANSPARENT|TOOLWINDOW|NOACTIVATE|TOPMOST`, per-pixel alpha via `UpdateLayeredWindow`), sized
  from `DWMWA_EXTENDED_FRAME_BOUNDS` (visible frame, not the invisible resize margin), outside the frame with the
  Win11 radius (8 px × monitor scale), inside the screen for maximized windows; thickness `Settings.BorderThickness`
  (2 px at 100 %), colour = Windows accent. Follows the target via a WinEvent hook **limited to the target's process**
  (location, show/hide, minimize, cloak = other virtual desktop, destroy) – a global LOCATIONCHANGE hook would also
  deliver every cursor move.
- **Border z-order: directly BELOW the target**, never above. With a topmost window directly above the target,
  `SetWindowPos(target, HWND_NOTOPMOST)` returns TRUE but silently keeps it topmost – unpinning would break.
  Below looks the same because the frame lies outside the window.
- **Sticky edges** (`EdgeSnapper`, `Settings.StickToEdges`, default on; threshold 24 px, target = work area inset by
  `Edges.PaddingAt96` = 8 px, both × monitor scale): the drag loop runs in the other process (WM_MOVING not
  reachable without injection) and places the window at cursor + fixed offset, so the cursor is what sticks. Between
  `EVENT_SYSTEM_MOVESIZESTART`/`END` (only with the left button down and when `WM_NCHITTEST` is not a resize border)
  a low-level mouse hook is installed on demand; it accumulates a virtual "raw" cursor from the real deltas, snaps the
  would-be visible frame to the padded work area of the nearest monitor, and when an adjustment is needed swallows
  the move and `SetCursorPos`es to the snapped spot. Leaving the zone (either side) rejoins the raw cursor → the
  window can pass the edge. Shift = no sticking. The cursor-to-frame offset is measured at the **first hook event**,
  not at MOVESIZESTART (the WinEvent is asynchronous and the window may still lag the cursor); a size change during
  the drag re-baselines. That measurement is still racy, so `Correct()` fixes the offset per axis while stuck: the
  cursor does not move on a stuck axis, the window settles, and two equal readings of frame − cursor become the offset.
- **Settle check** (`EdgeSnapper.Settle`, 60 ms after the last mouse event while stuck): people stop moving as soon
  as the window snaps, so without a next event the drag-lag error would stay. The settled window gives the true
  offset; the cursor is nudged so the window lands exactly (≤ 3 rounds).
- **Corner pin** (`Settings.AutoPinCorner`, default on, needs sticky edges; top-right and bottom-right –
  `Edges.IsDockedRightCorner`): on every `EVENT_SYSTEM_MOVESIZEEND` `TrayApp.OnDragEnded` pins a window that sits at
  a padded right-corner position (±2 px × scale, never maximized/minimized, so Windows Snap halves/quadrants that are
  flush do not count) and is not already topmost; an auto-pinned window that ended a drag elsewhere is unpinned, and
  the 2 s sweep unpins auto pins that left the corner any other way. Manual pins are never auto-released
  (`Pinner.Entry.Auto`).
- **Corner preview while dragging**: `EdgeSnapper` raises `CornerChanged(hwnd, inCorner)` (via BeginInvoke – the LL
  hook must return fast and a border creates a window) when the snapped would-be frame enters/leaves a padded corner.
  `Pinner.CornerPreview`: not pinned → a `PinBorder { Preview = true }` appears/disappears; auto-pinned → its border
  disappears/re-appears; manual pins ignore it. On drop `Pin()` adopts the preview border (no second animation).
- **Border animation** (same for manual and corner pins): visibility level 0→1 on `Appear` (350 ms) / 1→0 on
  `Disappear` (250 ms), reversible mid-way. Appear = thickness 4× → 1× with cubic ease-out, colour lightened 65 %
  towards white → accent, alpha up in the first quarter. Disappear = thickness swells to ~2.8×, brightens, fades.
  The border draws into ONE reused top-down DIB section wrapped by a `Format32bppPArgb` GDI+ bitmap (premultiplied,
  what `UpdateLayeredWindow` needs), sized to the frame + peak thickness so it is re-allocated only on target resize.
- **Performance**: no always-on global input hooks – the system-menu LL hooks exist only while one of our menus is
  open, the drag LL mouse hook only during a drag; no global `EVENT_OBJECT_SHOW` hook (fires for carets/tooltips
  everywhere); border hooks split into narrow per-process ranges (browsers fire focus/name/value events constantly);
  sweep timer and hover poll only run while something is pinned; `InvariantGlobalization`, `UseSystemResourceKeys`,
  GC + working-set trim. Measured: idle 0 ms CPU / 20 s, ~6 ms per drag, ~11 MB private, handles stable.
- **Hover reveal** (`HoverReveal`, `Settings.HoverReveal`, default on): cursor on the same title bar of a visible,
  non-topmost window that a pinned window overlaps for ~300 ms (moving along it allowed; an immediate reveal was
  distracting) → `Pinner.Lower` each overlapping pinned window: `HWND_NOTOPMOST`, then insert after the hovered window
  (both `SWP_ASYNCWINDOWPOS`, never blocks on a hung app) – the hovered window is neither activated nor moved. Title
  bar only, not the whole window. Cursor off that title bar for ~0.5 s (`LeaveTicks`) → `Pinner.Raise`; another
  covered title bar during the grace switches directly. No change while a mouse button is down. Lowered entries are
  skipped by the sweep and keep their border (`PinBorder.Held`). Tracking = 50 ms poll of
  `GetCursorPos`/`WindowFromPoint` while something is pinned; hit test only when the cursor moved. Measured: 0 ms CPU
  / 20 s mouse still, ~16 ms / 20 s mouse moving constantly.
- **Title-bar test**: first the geometric title STRIP (system title bar: down to the client top; app-drawn: down to
  the bottom of `DWMWA_CAPTION_BUTTON_BOUNDS`, deepened per window by every confirmed caption hit – app-drawn headers
  with search boxes or tabs are taller than their caption buttons, and their extra buttons answer HTCLIENT); strip =
  geometry only, no message to the app. Then `WM_NCHITTEST` (caption/sysmenu/min/max/close/help), asked of the window
  under the cursor first, then of the top-level window: apps that draw their title bar in a web view (new Teams,
  other WebView2/Electron apps) answer HTCLIENT on the top-level window but HTCAPTION on the drag-region child.
- **WM_NCHITTEST needs the target's coordinates** (`Native.NcHitTest`): this process is per-monitor aware and a SENT
  message is not DPI-virtualized, so for DPI-unaware/system-aware windows the physical point hits the wrong spot →
  `PhysicalToLogicalPointForPerMonitorDPI` first. Also used by the resize check in `EdgeSnapper`.
- **Taskbar guard** (`Pinner.KeepAboveTaskbar`): Windows inserts a newly activated window directly below the taskbar
  (`Shell_TrayWnd`, topmost). A window pinned from the system menu can end up BELOW the taskbar (the closing menu
  re-stacks it), and the taskbar raises itself when used – so the next activated window covered the "pinned" window
  although `WS_EX_TOPMOST` was set. 250 ms after a pin and on every foreground change, pinned windows found below the
  taskbar (walk `GW_HWNDPREV`) are re-raised with `HWND_TOPMOST`.
- **Separate taskbar buttons** (`TaskbarSplitter`, `Settings.SeparateTaskbarButtons`, off by default): the taskbar
  groups by AppUserModelID, settable per window from another process (`SHGetPropertyStoreForWindow` +
  `PKEY_AppUserModel_ID`, VT_LPWSTR to set, VT_EMPTY to clear – both take effect instantly). Per group key (window's
  own AppUserModelID, else process image path) the first window keeps its identity (pinned icon, jump list), the
  others get `AlwaysOnTop.Separate.<hwnd>`. New windows from `RegisterShellHookWindow` – no polling. Off / Exit clears
  every id it set. Skips ApplicationFrameWindow (UWP) and CoreWindow. Keeping a new window's button next to its
  app's other buttons was tried and was not reliable.
- **Compact Edge windows** (`EdgeAppMode`, `Settings.EdgeAppWindows`, default on): Edge cannot be put into a minimal
  mode from outside, so on `Pinner.Pinned` with auto = true (corner docks only – a menu pin keeps the browser window)
  of an Edge browser window (Chrome_WidgetWin_1 + msedge.exe): read the address bar with UI Automation off the UI
  thread (BFS over the control view, Document subtrees skipped, first Edit whose ValuePattern value looks like an
  address), start `msedge [--user-data-dir/--guest of the original process, from its command line via
  NtQueryInformationProcess 60] --profile-directory=<dir> --app=<url>` (profile: title "<page> - <profile> -
  Microsoft Edge" → Local State `profile.info_cache.*.shortcut_name`; "name" is only "Profile N"), wait up to 6 s for
  the new window, size it 412 × 512 DIP × monitor scale into the padded corner the browser was docked in (else at the
  browser's top-right, clamped to the work area), PIN IT FIRST, then unpin and MINIMIZE the original (never close).
  The order matters: `SetWindowPos(HWND_TOPMOST)` is silently refused while the original's fading topmost border sits
  next to the new window; one retry after 300 ms, else the app window is closed and the browser stays pinned.
  `Pinner.Released` of the app window (unpin, drag-out, closed, sweep, Exit) closes it and restores the original with
  SW_RESTORE (exactly its previous state). Windows without an address bar are left alone. `UseWPF` +
  `<Using Include="System.IO" />` for UIA.
- **Removed: frameless pinned windows** (tried, removed to keep behaviour consistent across apps). Findings, so it
  is not retried: classic windows CAN be made frameless from outside (remove WS_CAPTION|WS_THICKFRAME, SetWindowPos
  onto the former client rect with FRAMECHANGED; needs crash repair). Windows with an app-drawn title bar cannot: a
  window region from another process clips the client, but DWM keeps drawing the system backdrop over the whole
  window rect – an empty bar remains where the header was. DWM frame/backdrop attributes are own-process only.
- **Auto-update** (`Updater`, `Settings.AutoUpdate`, default on): asks `/releases/latest` of `Updater.Repository`
  (never drafts or pre-releases) 1 min after start and every 12 h. Install: SHA-256 check (asset `digest`, else the
  `.sha256` asset), exe extracted to `<exe>.new`, running exe renamed to `.old` (allowed while running), new one moved
  in, `Exit()` (un-pins everything), start `<exe> --after-update <pid>`, which waits for the old pid before the
  single-instance check, deletes `.old`, updates the installer's DisplayVersion and shows a balloon. Tray: "Install
  updates automatically", "Check for updates (version x)" (a manual check installs even when auto is off). The
  updater's own download has no Mark-of-the-Web, so SmartScreen does not interfere with updates.
- **Installer** (`installer/AlwaysOnTop.iss`, Inno Setup, built in CI): no wizard pages, per-user
  (`PrivilegesRequired=lowest`) into `%LOCALAPPDATA%\Always on Top` – the folder the updater writes to, no admin
  needed. Before copying it asks a running copy to `--exit` (then taskkill), starts the app afterwards. Missing .NET 10
  Desktop Runtime (`{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*`) → downloaded from
  `aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe` and run elevated (`/install /passive /norestart`).
  Uninstall stops the app, removes the Run value and the folder (settings, log). `AppId` = `Updater.InstallerAppId`,
  never change it.
- **Unpin on Exit**: windows this app pinned are un-pinned and all inserted items removed, so nothing is left behind.
- **Tray glyph**: Segoe Fluent Icons (Segoe MDL2 Assets fallback) at em = icon size, `SM_CXSMICON` at the taskbar's
  DPI, colour = taskbar foreground (`SystemUsesLightTheme`): E718 outline when nothing is pinned, E840 + E841 (fill
  layer) when something is. Re-rendered on theme/colour/display changes.
- **Menu**: native Win32 popup (`NativeMenu`) built fresh on every click, dark mode via uxtheme ordinals 135/136.
- **Autostart** defaults to on (`Settings.AutoStart = true`); the app writes the HKCU Run value on every start.
