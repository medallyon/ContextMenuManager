# Context Menu Manager

See and edit what's in the Windows File Explorer right-click menu. Pick a target (desktop, folder background, folder, file, drive), see the real menu Windows builds for it, click an item to find out where it's registered, and turn it off or back on.

Originally built as a PowerToys utility for [microsoft/PowerToys#33](https://github.com/microsoft/PowerToys/issues/33). That pull request was not accepted, so this is the standalone version.

## What it can turn off

- **Classic shell extension handlers** (`...\shellex\ContextMenuHandlers`), per user and for all users.
- **Classic static verbs** (`...\shell\<verb>`), including cascading submenu items.
- **Windows 11 packaged entries** (sparse-package context menu extensions).
- **The Windows 11 menu itself**: switch the default right-click menu back to the full classic menu.

No registry key is ever deleted, so every change can be undone. Explorer caches the menu, so changes show after File Explorer restarts (there's a button for that).

All-users entries need administrator rights. Use **Restart as administrator** in the app.

## Install

Download the zip for your machine (`win-x64` or `win-arm64`) from [Releases](https://github.com/medallyon/ContextMenuManager/releases), extract it and run `ContextMenuManager.exe`.

The exe is not code-signed yet, so Windows SmartScreen may show "Windows protected your PC" on first run. Click **More info**, then **Run anyway**. To skip the warning, unblock the zip before you extract it:

```powershell
Unblock-File .\ContextMenuManager-win-x64.zip
```

To check that a zip was built by this repo's GitHub Actions, compare it against `SHA256SUMS.txt` on the release, or run:

```powershell
gh attestation verify .\ContextMenuManager-win-x64.zip -R medallyon/ContextMenuManager
```

## Build

Needs the .NET 10 SDK.

```powershell
dotnet publish src/ContextMenuManager.App -c Release -r win-x64 -p:Platform=x64 -o publish
```

Use `win-arm64` and `-p:Platform=ARM64` for ARM. The output is self-contained: copy the folder and run `ContextMenuManager.exe`.

## Layout

- `src/ContextMenuManager.Core`: registry enumeration and toggling, packaged-entry enumeration, menu capture. No UI framework.
- `src/ContextMenuManager.MenuCapture`: console helper that builds the real Explorer menu in a separate process, so a crashing or hanging shell extension can't take the app down.
- `src/ContextMenuManager.App`: WinUI 3 app.
