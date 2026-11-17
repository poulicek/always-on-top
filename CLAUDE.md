# Always on Top – notes for Claude

Tray-only Windows app: adds a checkable "Always on Top" item to the system menu (title-bar right-click / Alt+Space)
of every top-level window, pins/unpins with `HWND_TOPMOST`, and draws an accent-coloured border around pinned windows.
Built 2026-10-03 as a personal tool, public on GitHub (`poulicek/always-on-top`, MIT) since 2026-11-17. Local,
machine-specific notes live in `CLAUDE.local.md` (git-ignored). When working from a packaged (MSIX) host such as the
Claude desktop app, writes to `%LOCALAPPDATA%`/`%APPDATA%` are redirected into the package's LocalCache – use the
`\\localhost\c$\...` admin-share path and launch the app via `explorer.exe` (see the guard in `Publish.ps1`).

## Stack and layout

- .NET 10 WinForms, TFM `net10.0-windows10.0.17763.0`, single-file framework-dependent win-x64 publish.
  Assembly name `Always on Top` (space on purpose: Task Manager name).
- `Program.cs` – single instance (mutex + process count), `--render-tray <dir>`, forwards `--menu|--exit|--status`.
- `TrayApp.cs` – ApplicationContext: NotifyIcon, native menu, settings, autostart, theme events.
- `SystemMenuHook.cs` – inserts the item into other windows' system menus and detects when it is chosen.
- `EdgeSnapper.cs` – magnetic screen edges while dragging any window; `Edges` (frame, scale, padded work area,
  top-right dock test). `Memory.cs` – GC + working-set trim after start-up and after the tray menu.
- `HoverReveal.cs` – hover a covered window's title bar → it shows above the pinned window until the cursor leaves.
- `TaskbarSplitter.cs` – optional: per-window AppUserModelID so each window gets its own taskbar button.
- `Pinner.cs` – pinned set, pin/unpin, 2 s sweep, borders on/off. `PinBorder.cs` – the border overlay window.
- `TrayIcons.cs` (Segoe Fluent Icons glyphs, PNG-ICO wrap, preview sheet), `NativeMenu.cs` (Win32 popup menu, as in
  Next Up), `Theme.cs` (taskbar/app theme, accent, dark menus, tray icon size), `Native.cs` (P/Invoke),
  `Settings.cs` (Paths, Log, Settings json), `Autostart.cs`, `Ipc.cs`, `HostForm.cs`.
- `tools/Make-Icon.ps1` (app.ico), `Publish.ps1`. Installed to `%LOCALAPPDATA%\Always on Top\` (exe,
  `settings.json`, `log.txt`).

## Workflow

- Build: `dotnet build -c Release`. Ship: `powershell -ExecutionPolicy Bypass -File Publish.ps1 -Launch`
  (asks the running app to `--exit`, publishes to `bin\publish`, copies via `\\localhost\c$`, launches via Explorer).
- Drive the running app with a second instance: `"...\Always on Top.exe" --menu|--exit|--status`
  (`--status` writes patched/pinned counts, pinned windows, settings and working set to the log).
- Log: `%LOCALAPPDATA%\Always on Top\log.txt` (from a packaged host via `\\localhost\c$\Users\<user>\...`).
- **Test safety (2026-10-07 incident)**: NEVER take a test window from `GetForegroundWindow()` after starting it –
  Windows does not always hand the foreground to the new process, and a test then moved, un-pinned and re-pinned the
  user's own Teams "Pinned window". Find test windows by the started process id (prefer the foreground window when
  it belongs to that pid – Character Map has a second top-level window – else require WS_CAPTION|WS_SYSMENU) (EnumWindows + pid), abort if not
  found, and only send keystrokes after verifying the foreground window is the test window. Keep test windows away
  from the user's topmost windows; before any mouse press check `WindowFromPoint` is the test window; close leftovers
  with `Get-Process charmap | Stop-Process`.
- Functional test pattern (2026-10-03): start `charmap.exe` (classic Win32 window), take the foreground window,
  `keybd_event` Alt+Space then `T`, check `WS_EX_TOPMOST`; mouse path: `GetMenuItemRect` of id `0x7A10` +
  `SetCursorPos` + `mouse_event`. Test scripts must call `SetProcessDpiAwarenessContext(-4)` (screen is scaled).
  In PowerShell P/Invoke pass `[NullString]::Value`, not `$null`, for a null string (`$null` becomes `""`).

## Design decisions (and why)

- **No DLL injection.** The item is inserted cross-process (`GetSystemMenu(hwnd, FALSE)` + `InsertMenuItem`; menu
  handles are global). The resulting `WM_SYSCOMMAND` goes to the target's wndproc, which we cannot see, so the choice
  is detected while the menu is open: `EVENT_SYSTEM_MENUPOPUPSTART` → `MN_GETHMENU` identifies our menu; a
  low-level mouse hook (`MenuItemFromPoint` on button-up), a low-level keyboard hook (Enter/Space with our item
  `MF_HILITE`, or `T`). The target ignores the unknown command id. Ids: item `0x7A10`, separator `0x7A20`
  (< 0xF000, low 4 bits zero). The item goes just above *Close* with its own separator.
- **When to insert**: existing windows at start (EnumWindows), `EVENT_OBJECT_SHOW`, `EVENT_SYSTEM_FOREGROUND` plus a
  400 ms retry (dialogs are activated before they are visible – v1 missed Character Map), `EVENT_SYSTEM_MENUSTART`
  (late but still in time for the popup in practice).
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
  `SetWindowPos(target, HWND_NOTOPMOST)` returns TRUE but silently keeps it topmost – unpinning (from our menu or any
  other tool) broke (found and verified 2026-10-03). Below looks the same because the frame lies outside the window.
- **Sticky edges** (`EdgeSnapper`, `Settings.StickToEdges`, default on, 2026-10-03; threshold 24 px, target = work
  area inset by `Edges.PaddingAt96` = 8 px, both × monitor scale – user asked for a larger zone and a small gap): the drag loop runs in the other
  process (WM_MOVING not reachable without injection) and places the window at cursor + fixed offset, so the cursor
  is what sticks. Between `EVENT_SYSTEM_MOVESIZESTART`/`END` (only with the left button down and when `WM_NCHITTEST`
  is not a resize border) a low-level mouse hook is installed on demand; it accumulates a virtual "raw" cursor from
  the real deltas, snaps the would-be visible frame (`DWMWA_EXTENDED_FRAME_BOUNDS`) to the work area of the nearest
  monitor within 16 px × monitor scale, and when an adjustment is needed swallows the move and `SetCursorPos`es to
  the snapped spot. Leaving the zone (either side) rejoins the raw cursor → the window can pass the edge. Shift =
  no sticking. The cursor-to-frame offset is measured at the **first hook event**, not at MOVESIZESTART: the WinEvent
  is asynchronous and the window may still lag the cursor (snapped to −3 px instead of 0 before the fix). A size
  change during the drag (maximized window restored by dragging) re-baselines. That first-event measurement is
  still racy (user saw the gap "work randomly"), so `Correct()` fixes the offset per axis while stuck: the cursor
  does not move on a stuck axis, the window settles, and two equal readings of frame − cursor become the offset.
  Verified: gap exactly 12 px (= 8 × 1.5; this monitor is 144 dpi) on 4 of 4 runs, left and bottom edge.
- **Corner pin** (`Settings.AutoPinCorner`, default on, needs sticky edges; top-right, and since 2026-10-07 also
  bottom-right – `Edges.IsDockedRightCorner`, `EdgeSnapper` corner test accepts either): on every `EVENT_SYSTEM_MOVESIZEEND`
  `TrayApp.OnDragEnded` pins a window that sits at the padded top-right position (`Edges.IsDockedRightCorner`, ±2 px ×
  scale, never maximized/minimized, so Windows Snap halves/quadrants that are flush do not count) and is not already
  topmost; an auto-pinned window that ended a drag elsewhere is unpinned, and the 2 s sweep unpins auto pins that left
  the corner any other way (keyboard, maximize). Manual pins are never auto-released (`Pinner.Entry.Auto`).
- **Border animation** (2026-10-03, user asked for "more vivid", same for manual and corner pins): visibility level
  v 0→1 on `Appear` (350 ms) / 1→0 on `Disappear` (250 ms), reversible mid-way. Appear = thickness 4× → 1× with
  cubic ease-out, colour accent lightened 65 % towards white → accent, alpha up in the first quarter. Disappear =
  thickness swells to ~2.8×, brightens, alpha fades. Thickness animation re-draws every 15 ms frame, so the border
  draws into ONE reused top-down DIB section wrapped by a `Format32bppPArgb` GDI+ bitmap (premultiplied, what
  `UpdateLayeredWindow` needs), sized to the frame + peak thickness margin so it is re-allocated only on target
  resize. Verified by sampling a pixel 1 px and 6 px outside the frame: burst reaches 6 px only in the first ~60 ms
  of a pin, colour settles at accent by ~330 ms; unpin lights the 6 px pixel 90–220 ms, gone by ~270 ms.
- **Corner preview while dragging**: `EdgeSnapper` raises `CornerChanged(hwnd, inCorner)` (via BeginInvoke – the LL
  hook must return fast and a border creates a window) when the SNAPPED would-be frame enters/leaves the padded
  top-right spot. `Pinner.CornerPreview`: not pinned → a `PinBorder { Preview = true }` (shown although the window is
  not topmost) appears/disappears; auto-pinned → its border disappears/re-appears; manual pins ignore it. On drop
  `Pin()` adopts the preview border (no second animation); `EndPreview` cleans up otherwise.
- **Settle check** (`EdgeSnapper.Settle`, 60 ms after the last mouse event while stuck): people stop moving as soon
  as the window snaps into the corner, so without a next event the drag-lag error stayed (window parked 19/20 px
  instead of 12 and the drop did not count as docked). The settled window gives the true offset; the cursor is
  nudged so the window lands exactly (≤3 rounds).
- **Performance pass** (2026-10-03, user request): no always-on global input hooks – the system-menu LL mouse /
  keyboard hooks exist only while one of our patched menus is open, the drag LL mouse hook only during a drag;
  global `EVENT_OBJECT_SHOW` hook removed (fires for carets/tooltips everywhere); border hooks split into four narrow
  per-process ranges instead of 0x8001..0x8018 (browsers fire focus/name/value events constantly); sweep timer only
  runs while something is pinned; `InvariantGlobalization`, `UseSystemResourceKeys`, GC + working-set trim.
  Measured: idle 0 ms CPU / 20 s, 0 ms during 10 s of synthetic mouse+keyboard input, ~6 ms per drag, 11 MB private,
  handles stable over 20 drags.
  Test: `drag` script pattern – charmap at 60 px from the edge, `mouse_event` down on the caption, small wiggle,
  40 relative steps of 4 px, log frame-to-work-area gap per step; expected `… 31 0 0 0 0 0 0 0 -26 …`.
- **Hover reveal** (`HoverReveal`, `Settings.HoverReveal`, default on, 2026-10-07): cursor resting ~300 ms on the
  title bar (`WM_NCHITTEST` ∈ caption/sysmenu/min/max/close/help) of a visible, non-topmost window of another app
  that overlaps pinned windows → `Pinner.Lower` each overlapping pinned window: `HWND_NOTOPMOST` then insert after
  the hovered window (both `SWP_ASYNCWINDOWPOS`, never blocks on a hung app) – the hovered window is neither
  activated nor moved, the pinned window is the only one that changes place. Cursor off the hovered window's TITLE
  BAR for ~150 ms (user wanted the heading only, not the whole window – 2026-10-07; re-hit-tested when the cursor
  moves) → `Pinner.Raise` (`HWND_TOPMOST`). Timing changed 2026-10-20 (user; the immediate reveal was then found distracting and the 300 ms delay
  restored - now: on the same covered title bar for 300 ms, moving along it allowed): previously reveal immediately on the
  first poll that sees the cursor on the title bar, 0.5 s grace (`LeaveTicks` = 10) after leaving it; another covered
  title bar during the grace switches directly. Title-bar test (2026-10-20, user: extra buttons in a title bar dropped the
  peek): first the geometric title STRIP (system title bar: down to the client top; app-drawn: down to the bottom of
  DWMWA_CAPTION_BUTTON_BOUNDS, deepened per window by every confirmed WM_NCHITTEST caption hit - Teams/Edge headers are
  taller than their caption buttons), then WM_NCHITTEST as before. Strip = geometry only, no message to the app, no
  extra polling. Read-only check on the user's windows: Teams/To Do/Edge 30 px strips. Not verified on screen (the
  user was working; Windows refused the test window the foreground). No change while a mouse button is down. Lowered entries are skipped by
  the sweep, keep their border (`PinBorder.Held`; the border also leaves/joins the topmost band with its target).
  Tracking = 50 ms poll of `GetCursorPos`/`WindowFromPoint`, running only while something is pinned; hit test only
  when the cursor moved. Measured: 0 ms CPU / 20 s mouse still, ~16 ms / 20 s mouse moving constantly.
- **Custom title bars** (2026-10-12, user: peek did not work on Teams): new Teams' top-level `TeamsWebView` answers
  HTCLIENT over its own title strip; the child under the cursor (`Chrome_WidgetWin_0`, the WebView2 drag-region
  window, top 51 px = DWM caption-button height) answers HTCAPTION. `HoverReveal.IsCaption` asks the window under the
  cursor first, then the top-level one. Probed read-only on the user's Teams windows. Not end-to-end tested on Teams:
  the only visible Teams window was a live meeting's compact view, which is itself topmost and therefore excluded.
- **WM_NCHITTEST needs the target's coordinates** (`Native.NcHitTest`): this process is per-monitor aware, a SENT
  message is not DPI-virtualized, so for DPI-unaware/system-aware windows (Character Map at 150 %) the physical
  point hit the wrong spot → `PhysicalToLogicalPointForPerMonitorDPI` first. Also fixes the resize check in
  `EdgeSnapper` (it silently treated resize borders of such apps as drags).
- **Taskbar guard** (`Pinner.KeepAboveTaskbar`, 2026-10-07): Windows inserts a newly activated window directly below
  the taskbar (`Shell_TrayWnd`, topmost). Pinning from the system menu left the pinned window BELOW the taskbar (the
  closing menu re-stacks it), and the taskbar raises itself when used – so the next activated window covered the
  "pinned" window although `WS_EX_TOPMOST` was set. 250 ms after a pin and on every foreground change, pinned windows
  found below the taskbar (walk `GW_HWNDPREV`) are re-raised with `HWND_TOPMOST`. The border was suspected first and
  ruled out by a control run (shell-made topmost window, no border, placed below the taskbar → same failure).
- **Separate taskbar buttons** (`TaskbarSplitter`, `Settings.SeparateTaskbarButtons`, OFF by default, 2026-10-13):
  the taskbar groups by AppUserModelID, settable per window from another process (`SHGetPropertyStoreForWindow` +
  `PKEY_AppUserModel_ID`, VT_LPWSTR to set, VT_EMPTY to clear – both take effect instantly). Per group key (window's
  own AppUserModelID, else process image path) the first window keeps its identity (stays on the pinned icon, jump
  list), the others get `AlwaysOnTop.Separate.<hwnd>`. New windows from `RegisterShellHookWindow`
  (HSHELL_WINDOWCREATED/DESTROYED on the host form) – no polling. Off / Exit clears every id it set. Skips
  ApplicationFrameWindow (UWP) and CoreWindow. Dev commands `--split-on` / `--split-off`.
  Verified with Character Map + UIA reading of taskbar button names ("… - 2 running windows" → two "… - 1 running
  window"; a window opened while on is separated at once; off merges back; the user's other buttons unchanged).
  Not checked: how packaged desktop apps (new Teams, new Outlook) look when separated (icon could fall back).
- **Removed: frameless pinned windows** (built 2026-10-13 as an experiment, removed 2026-10-15 at the user's request to
  keep the app consistent, since it could only ever work for some apps). Findings, so it is not retried:
  - Classic windows (system-drawn title bar) CAN be made frameless from outside: remove WS_CAPTION|WS_THICKFRAME
    (SetWindowLongPtr) and SetWindowPos onto the former client rect with FRAMECHANGED - content stays put, restoring the
    style and insets gives the exact original rect. Needs crash repair (a killed app leaves windows frameless).
  - Windows with an app-drawn title bar (Teams, Edge, Office, VS Code) cannot: a window region from another process
    clips the client, but DWM keeps drawing the system backdrop (Mica or its inactive fallback colour) over the whole
    window rect - an empty bar remains where the header was (verified by pixel sampling). For classic windows a region
    leaves the DWM frame and title bar visible. DWM frame/backdrop attributes are own-process only. WS_EX_LAYERED from
    outside was not tried (risk for GPU-composited meeting video). No special treatment for Teams windows.
- **Auto-update** (`Updater`, `Settings.AutoUpdate`, default on, 2026-11-17): GitHub Releases of
  `Updater.Repository`. `.github/workflows/release.yml` builds every pushed `v*` tag with `-p:Version=<tag>` and
  publishes `AlwaysOnTop-win-x64.zip` + `.sha256` as a PRE-release; the app asks `/releases/latest` (never drafts or
  pre-releases) 1 min after start and every 12 h, so a release reaches installs only once it is promoted to Latest.
  Install: SHA-256 check (asset `digest`, else the `.sha256` asset), exe extracted to `<exe>.new`, running exe renamed
  to `.old` (allowed while running), new one moved in, `Exit()` (un-pins everything), start
  `<exe> --after-update <pid>`, which waits for the old pid before the single-instance check, deletes `.old` and shows
  a balloon. Tray: "Install updates automatically", "Check for updates (version x)" (a manual check installs even when
  auto is off and reports the result in a balloon). Release = `git tag v1.2.0` + `git push origin v1.2.0`, then
  promote on GitHub. Local `Publish.ps1` builds carry the csproj `<Version>` and get replaced by any higher promoted
  release. Unsigned exe: SmartScreen warns on a manual browser download; the updater's own download has no
  Mark-of-the-Web, so updates are not affected.
- **Unpin on Exit**: windows this app pinned are un-pinned and all inserted items removed, so nothing is left behind.
- **Tray glyph**: Segoe Fluent Icons (Segoe MDL2 Assets fallback) at em = icon size, `SM_CXSMICON` at the taskbar's
  DPI, colour = taskbar foreground (`SystemUsesLightTheme`): E718 outline when nothing is pinned, E840 + E841 (fill
  layer) when something is. Re-rendered on theme/colour/display changes.
- **Menu**: native Win32 popup (`NativeMenu`) built fresh on every click, dark mode via uxtheme ordinals 135/136.
- **Autostart** defaults to on (`Settings.AutoStart = true`); the app writes the HKCU Run value on every start.
