# Always on Top

A tiny Windows 11 tray app that adds **Always on Top** to the title-bar menu of every window.

- **Pin a window**: right-click its title bar (or press Alt+Space) and choose *Always on Top* – a check mark shows
  the current state; choose it again to unpin. In the open menu, the **T** key does the same.
- **Border**: pinned windows get a thin frame in your Windows accent colour (rounded like Windows 11 windows), so it
  is obvious which ones stay on top. Pinning bursts it in thick and bright and settles it to the thin line;
  unpinning swells and fades it (same effect for manual and corner pins). Can be switched off in the tray menu.
- **Sticky screen edges**: while you drag any window, it snaps to a small gap (8 px at 100 % scaling) from the
  screen edge (above the taskbar) once its edge gets within ~24 px of that spot. Keep pushing and it breaks free, so
  windows can still be moved past the edge. Hold **Shift** while dragging to move without sticking. Can be switched
  off in the tray menu.
- **Corner pin**: drag a window into the top-right or bottom-right corner (it sticks there with the gap on both sides) – the border
  appears while you are still holding the mouse, showing that releasing will pin it. Drag a corner-pinned window out
  and the border disappears before you release, showing it will be unpinned. Moving it away by keyboard or
  maximizing unpins it as well.
  Windows you pinned yourself are never unpinned this way. Can be switched off in the tray menu.
- **Peek over pinned windows**: keep the cursor on the title bar of a window that a pinned window covers for ~0.3 s,
  and that window comes up above the pinned one (extra buttons, search boxes and tabs in the title bar count too); it stays up while the cursor is on that title bar and for about half a
  second after it leaves (so a brief slip off the title bar does not drop it), then the pinned window is back on top.
  Nothing gets activated and no other window changes its place. Can be switched off in the tray menu.
- **Separate taskbar buttons** (optional, off by default): with "combine taskbar buttons" on, every window of an app
  gets its own taskbar button instead of being stacked on the app's icon – e.g. two Word documents, two icons. The
  first window of each app stays on its normal (pinned) icon with its jump list; the others get their own buttons,
  added at the end of the taskbar, without a jump list. Store/UWP apps and windows running as administrator are left
  as they are. Switching it off (or exiting) merges everything back.
- **Compact Edge windows** (on by default): docking a Microsoft Edge window into a right-hand corner opens the current
  tab as a compact Edge app window (thin title bar only – no tabs, no address bar; 412 × 512 at 100 % scaling, scaled
  with the display) in the same profile, pins that instead and minimizes the browser window (it is never closed).
  Unpinning closes the app window and restores the browser window exactly as it was. The page loads again in the app
  window (logins are kept; video position or unsaved input are not carried over). Pinning Edge from the title-bar menu
  keeps the normal browser window. Can be switched off in the tray menu.
- **Tray icon**: a pin from the Windows icon font, white on a dark taskbar and black on a light one – an outline when
  nothing is pinned, filled while at least one window is pinned.
- **Tray menu** (left or right click, native Windows menu, follows light/dark mode): the pinned windows (click one to
  unpin it), *Unpin all*, *Show border around pinned windows*, *Show hovered windows above pinned ones*, *Stick windows to screen edges*, *Pin windows docked in a right-hand corner*, *Separate taskbar button for every window*, *Show corner-pinned Edge tabs as compact app windows*, *Start with Windows* (on by default), *Install updates automatically* (on by default), *Check for updates*, *Exit*.
- **Exit** un-pins the windows the app pinned and removes the menu item from all windows.

## Resource use

Measured 2026-10-03: 0 ms CPU while idle and while typing / moving the mouse (no global input hooks outside an open
title-bar menu or a window drag), ~6 ms CPU per window drag, ~11 MB private memory, stable handle count.
With a window pinned (cursor poll for the hover peek active): 0 ms / 20 s with the mouse still, ~16 ms / 20 s with
the mouse moving constantly (2026-10-07).

## Install

Download **[AlwaysOnTop-Setup.exe](https://github.com/poulicek/always-on-top/releases/latest/download/AlwaysOnTop-Setup.exe)**
and run it. There are no questions to answer: it installs for the current user (no administrator rights needed) into
`%LOCALAPPDATA%\Always on Top\`, adds a Start Menu shortcut, starts the app and sets it to start with Windows (can be
turned off in the tray menu). If the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) is
missing, the setup downloads it from Microsoft and installs it first (Windows asks for permission once).
Uninstall from *Settings → Apps*.

The setup is not code-signed, so Windows SmartScreen may warn before the first run (*More info* → *Run anyway*).

Without the setup: download `AlwaysOnTop-win-x64.zip` from the
[latest release](https://github.com/poulicek/always-on-top/releases/latest), extract `Always on Top.exe` anywhere and
run it (needs the .NET 10 Desktop Runtime).

From source: `powershell -ExecutionPolicy Bypass -File Publish.ps1 -Launch` builds it, installs it to
`%LOCALAPPDATA%\Always on Top\`, adds a Start Menu shortcut and starts it.

## Updates

The app checks the latest GitHub release a minute after start and every 12 hours. When a newer version is out it
downloads it, verifies its SHA-256 checksum, replaces itself and restarts (pinned windows are released; the new
version starts fresh). Pre-releases are never installed. Turn it off with *Install updates automatically*, or check
right away with *Check for updates*.

Maintainers: push a version tag (`git tag v1.2.0`, `git push origin v1.2.0`); the workflow publishes a pre-release,
and promoting it to *Latest* on GitHub ships it to every installed copy.

## Limitations

- Windows running as administrator can only be pinned when *Always on Top* runs as administrator too (a tray
  notification says so).
- Apps that draw their own title-bar menu (Settings, Snipping Tool and other WinUI/UWP apps) do not get the item:
  Windows rejects other processes changing their menu (`ERROR_INVALID_MENU_HANDLE`).

## Build

.NET 10 SDK: `dotnet build -c Release`. Tray glyph review: `"Always on Top.exe" --render-tray <dir>`.
