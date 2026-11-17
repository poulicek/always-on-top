# Rebuilds "Always on Top.exe", installs it to %LOCALAPPDATA%\Always on Top and refreshes the Start Menu shortcut.
# Usage:  powershell -ExecutionPolicy Bypass -File Publish.ps1 [-Launch] [-Version 1.2.3]   (default: csproj <Version>)
param([switch]$Launch, [string]$Version)
$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$appName = 'Always on Top'
$dest    = Join-Path $env:LOCALAPPDATA $appName
$exe     = Join-Path $dest "$appName.exe"
$startLnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$appName.lnk"

# --- MSIX virtualization guard ---------------------------------------------------------
# From a packaged host (the Claude desktop app's tool shell) writes to %LOCALAPPDATA% / %APPDATA% are silently
# redirected into ...\Packages\<pkg>\LocalCache\. The admin share bypasses that, so all install writes go via Physical().
function Physical([string]$path) {
    $share = '\\localhost\' + $path.Substring(0, 1) + '$'
    if (Test-Path -LiteralPath $share) { return $share + $path.Substring(2) }
    return $path
}

# Ask the running app to exit (it un-pins its windows and removes its menu items), then make sure it is gone.
if (Test-Path -LiteralPath (Physical $exe)) {
    try { & (Physical $exe) --exit | Out-Null } catch {}
    Start-Sleep -Milliseconds 800
}
Get-Process -Name $appName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

# 1) icon + build into a staging folder
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\Make-Icon.ps1')
$staging = Join-Path $root 'bin\publish'
[string[]]$extra = @(); if ($Version) { $extra += "-p:Version=$Version" }
dotnet publish (Join-Path $root 'AlwaysOnTop.csproj') -c Release -o $staging @extra
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 2) install to the physical %LOCALAPPDATA% location
$destP = Physical $dest
New-Item -ItemType Directory -Force -Path $destP | Out-Null
Copy-Item -LiteralPath (Join-Path $staging "$appName.exe") -Destination (Join-Path $destP "$appName.exe") -Force

# 3) Start Menu shortcut (the app registers its own Run key for autostart)
$ws = New-Object -ComObject WScript.Shell
$lnkP = Physical $startLnk
$s = $ws.CreateShortcut($lnkP)
$s.TargetPath = $exe
$s.WorkingDirectory = $dest
$s.IconLocation = "$exe,0"
$s.Description = 'Adds "Always on Top" to the title-bar menu of every window'
$s.Save()
$check = $ws.CreateShortcut($lnkP).TargetPath
if ($check -like '*\Packages\*') { Write-Warning "Shortcut target resolves into a package LocalCache: $check" }
Write-Host "Shortcut:  $startLnk -> $check"

Write-Host "Installed: $exe"
if ($Launch) {
    # Through Explorer so the app is NOT virtualized (real profile folder, real registry, visible to the user).
    Start-Process -FilePath "$env:SystemRoot\explorer.exe" -ArgumentList "`"$startLnk`""
}
