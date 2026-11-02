# Capy Bara's Mod Manager

A Windows desktop app for MX Simulator with capybara mascots, oranges and a warm citrus theme. Extract the complete portable app ZIP, then run `CapyBaraModManager.exe` from the extracted folder. Keep all accompanying DLLs and the `runtimes` and `licenses` folders together.

## Download the app

**[Download the Windows app, version 1.3.0](https://github.com/gsshunter-models/capy-baras-mod-manager/releases/download/v1.3.0/CapyBaraModManager-v1.3.0-windows-portable.zip)**

If the direct download does not start, open the [release page](https://github.com/gsshunter-models/capy-baras-mod-manager/releases/tag/v1.3.0) and choose **CapyBaraModManager-v1.3.0-windows-portable.zip** under **Assets**. Extract the full ZIP before running the app. The [download checksum](https://github.com/gsshunter-models/capy-baras-mod-manager/releases/download/v1.3.0/SHA256SUMS.txt) is attached to the release too.

Requires Windows, the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/9.0) and [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/). The .NET runtime is not included in this portable build. The source code, synthetic tests, mascot and third-party notices are included in this repository; MX Simulator and downloaded game mods are separate.

Version 1.3.0 renames the app and its Desktop shortcut. It continues using the existing `%LOCALAPPDATA%\MXSimModManager` data folder, so library settings, groups, downloads, setup backups and undo history carry over. The previous portable app remains available through **Previous version.lnk** in the new app folder.

## Library

- Inventories personal SAF packages and loose mod folders, with tracks, rider gear/helmets, bikes/wheels and other assets.
- Reads SAF indexes directly; it does not decrypt protected models or textures.
- Search names and paths, sort by name/size/track count/modified date, and create named groups. Ctrl/Shift selects several packages to group at once. Groups are virtual and do not move game files. A SAF containing several tracks remains one package.
- Track totals count distinct virtual terrain paths. Detection does not verify that every dependency is installed or that nested track folders are correctly arranged.
- Base-game content is optional and read-only. Loose individual files in the library root are grouped for inspection and managed manually.
- The library refreshes after file changes. Export inventory as CSV.

## Install and revert

Choose a ZIP, SAF, RAR, 7z, or extracted mod folder, or drop it onto the app. Review destinations and replacements before installing. Executables, DLLs, scripts and game credentials/config are excluded from mod installs. Password-protected and incomplete multipart archives need to be extracted separately first.

Track package folders are installed intact, including folders containing track SAF files, their asset subfolders and sibling shared object folders. Only outer download wrappers are removed when the track package boundaries can be detected. For example, `Download/TrackName/terrain.hf` installs as `TrackName/terrain.hf`, and `Download/TrackName/TrackName.saf` keeps the SAF inside `TrackName`. Selecting a track folder directly keeps its name. A loose track at an archive's root gets a folder named after the archive. The preview identifies the folders being preserved; existing installed mods are not reorganized automatically.

New files may be installed while the game is open. Close the game before replacing files, disabling/enabling a package, or undoing an installation. Replaced files are backed up. **Undo last install** restores originals and removes newly installed files; it stops if installed content has since been edited. Enable a disabled package before undoing its installation.

Disabling stores the complete package outside the game library. Shared model/asset dependencies can affect other mods, so enable it again if another mod relies on it.

## Download forum tracks

Open **Download forum tracks** from the sidebar. The in-app browser opens `https://forum.mxsimulator.com/viewforum.php?f=4`. The search box uses Google restricted to forum posts; **Bing** provides an alternative when a provider is unavailable. This avoids relying on the forum's own search page. Release posts remain on the MX Simulator forum. Open a post and follow the author's download link. You can paste a forum topic or download-host URL into the address bar too.

ZIP, SAF, RAR and 7z downloads appear in the download list with progress. Once ready, select one and choose **Install selected**. The same destination preview, original-file backups and undo apply as for local installs. Downloading never installs automatically. Files remain in the manager's downloads folder so you can install them later.

**Skip download pages** is enabled by default. Public MediaFire, MEGA, Dropbox and direct archive links clicked in a forum post download directly into the manager. **Paste download link** accepts a copied author link as well. MEGA links need the full `#` key; folders show a package chooser, and file/subfolder selections in the link are respected. MEGA decryption and integrity verification happen locally using [MegaApiClient](https://github.com/gpailler/MegaApiClient). MEGA account login is not required for public links. Link keys and tokens are not saved in the manager's download history.

Direct transfers use a separate temporary file, verify the advertised size when available, and publish the completed file only after successful reading and verification. Cancelling or failing a transfer never leaves a partial package ready to install. The limit is three simultaneous downloads and 40 GB per package. Downloads can be retried by clicking or pasting the link again.

Some download hosts require manual sign-in, a captcha, or other download steps. These remain on the host's page; the app does not bypass them. Executable downloads and external application launch links are blocked. Broken or removed forum links cannot be recovered by the manager.

**Open download page** opens the selected link in the embedded browser and turns off direct interception, so you can complete normal host steps. Turn **Skip download pages** back on to resume direct downloading. Unsupported hosts use the ordinary embedded browser. A MEGA quota or unavailable-link error requires the host's normal wait or a working author link.

## Play an installed track

Select a track package, then choose **Play track** in its details panel. Multi-track packages open a layout picker. The game launches directly on the chosen virtual track folder using its native `--track` option. Disabled packages cannot be played.

If MX Simulator is already open, the app asks before restarting it on the selected track. Save your replay and setup first; quit through the game's menu first if you want live settings saved. The app requests a normal close and never forcibly terminates the game.

Reference: [MX Simulator launch options](https://forum.mxsimulator.com/viewtopic.php?t=13510).

## Refreshing MX Simulator

The manager's **Refresh library** updates its own inventory. MX Simulator builds its file index at startup; no supported full library rescan was verified. **Restart to load new content** requests a normal close, waits, and relaunches the game after confirmation. Save your replay first. The app never forcibly terminates the game.

**Reload current track** copies `/forcereload`; paste it into the game's chat and restart the race. This reloads the current track and does not promise that new track, gear or bike packages will appear without restarting the game.

Developer reference: [current-track reload](https://forum.mxsimulator.com/viewtopic.php?t=55058).

## Setup manager

Browse native setups, edit bike/handling values, save presets or save copies, and import/export native setup files. Existing setup files are backed up before an edited preset replaces them. Only supported bike/handling settings are edited and applied; authentication, rider identity, key bindings and graphics are excluded from saved edited presets.

Apply a saved preset to the game config with the game closed. The current config is backed up before every apply or restore. Use **Restore config backup** to revert. To use a native preset while playing, copy `/readsetup NAME` and paste it into the game's chat. Existing unedited native presets can contain additional settings; the UI identifies these.

The game may have unsaved settings while it runs. Use `/writesetup NAME` in game, then refresh the manager to capture those live values. Loading the disk config into the editor reads its last saved values.

Developer reference: [native setup commands](https://forum.mxsimulator.com/viewtopic.php?t=23206).

## Bike and rider appearance

Use **Bike & rider appearance** for dropdowns listing installed bike models/dynos, matching bike skins, helmets, rider gear and wheels. Select a bike first to filter bike skins to its model family. Normal/specular maps are excluded from skin choices. Existing saved selections remain visible if an asset cannot be found, so scanning never resets your look.

Skin choices require matching models. Player assets stored as loose files are indexed at the personal folder root; SAF packages can contain their own virtual directories.

Apply to the saved game config with the game closed; a complete config backup is created first. Appearance presets are native setup files containing only the five bike/appearance fields. They exclude suspension, gearing, stability, rider number and all other setup values. Saving an appearance preset requires a new name and never replaces an existing setup file. Save a preset and copy its `/readsetup NAME` command to use it while playing. New asset packages may still need a game restart before the game can use them. Use Restore config backup to revert an applied look.

The appearance menu blocks copying load commands for full native setups that contain other values. Load their appearance into the editor and save a new appearance-only preset first. Full bike setups remain available through Setup manager.

## Local storage and source

Default personal library: `%LOCALAPPDATA%\MX Simulator`.

Manager settings, groups, backups, install receipts and disabled packages: `%LOCALAPPDATA%\MXSimModManager`. Setup backups are kept separately for each personal library under `libraries\<library id>\setup-backups`; the setup window's **Open backups** opens the correct folder. Keep the manager storage folder to retain undo/backups. Removing the portable app does not remove or restore your mods automatically.

The manager does not patch `mx.exe`, inject code, replace OpenGL DLLs, or alter the existing camera/ReShade add-ons. The manager adds no telemetry. Forum browsing contacts the websites you visit and uses its own WebView2 browser profile under `forum-browser`; it does not import your existing browser's cookies. Downloaded packages and their history are under `downloads` and `downloads.json`.

Build: `dotnet build Desktop/MXSimModManager.csproj -c Release`.

Tests: `dotnet run --project Tests/MXSimModManager.Tests.csproj -c Release`. Tests use generated temporary libraries; `-- --scan-real` adds an optional read-only scan of the current personal library. `-- --resolve-link URL --download-if-under BYTES` optionally checks a public host link and downloads small packages into the disposable test area for preview only. Builds restore pinned WebView2, SharpCompress, MegaApiClient and Newtonsoft.Json NuGet packages. Their notices are included in the portable app's `licenses` folder.
