# SteamGridDB for Xbox

> **Community fork (1.6.3)** of [lirrik/steamgriddb-xbox](https://github.com/lirrik/steamgriddb-xbox) with fixes for games going missing from the list and artwork changes not sticking, plus extra square artwork sources.
>
> **Fixed**
> - **Games missing or a different number found on every refresh** ([upstream #7](https://github.com/lirrik/steamgriddb-xbox/issues/7)): one unreadable manifest entry or locked image used to silently drop every remaining game from that store. Each entry is now handled on its own, manifests are re-read if the Xbox app is mid-write, and refreshes can no longer overlap.
> - **Artwork changes not applying or being reset**: downloaded images (often JPEG, WebP or ICO) were saved as-is under a `.png` name. Every image is now decoded, cropped to a square and re-encoded properly. Customised files are also made read-only, so the Xbox app can no longer overwrite them (you can turn this off in Settings).
> - The widget kept every cover image file open while it ran, which blocked the Xbox app from updating them. Images are now loaded into memory.
> - Epic games matched on SteamGridDB but then showed no artwork, because the wrong ID was used for the artwork lookup. Lookups now use the SteamGridDB game ID.
> - Backups for custom games with non-`.png` images overwrote the original.
> - Without a `STEAMGRIDDB_API_KEY` environment variable, nothing loaded at all. There is now a Settings dialog for the key, and the list loads without one.
> - SteamGridDB rate limiting during big library loads is retried, and name lookups run in parallel.
>
> **New**
> - **Square artwork from the Xbox Store (box art), PlayStation Store (master art) and IGN**, shown next to SteamGridDB results with a source badge. Search uses the same public endpoints as the Playnite [XboxMetadata / IgnMetadata](https://github.com/Jeshibu/PlayniteExtensions) extensions and the PlayStation Store website.
> - "Fix my library" falls back to store artwork when SteamGridDB has nothing and the store title matches exactly.
> - **Add missing Steam games** (+ button): finds games installed in Steam that the Xbox app doesn't list and adds them to its custom library. It also lists the games the Xbox app did detect, unticked: tick any you can't see, because the Xbox app hides some detected games even when they have a cover (for example EA SPORTS FC 27). They launch through Steam (`steam.exe steam://rungameid/<id>`) rather than the game's exe, and get SteamGridDB or store cover art. The Xbox app is closed while its library is updated, and the widget waits until all of its processes (including the tray icon) have exited, because any one left running saves over the change. Windows asks once whether the widget may do this; otherwise it asks you to close the Xbox app, and the previous library file is backed up first. The custom library format follows [xbox-rom-manager](https://github.com/izzyjade06/xbox-rom-manager). It is not officially documented, so future Xbox app updates may change it.
> - Manual search has a "store artwork only" result, so it works even without a SteamGridDB key.
> - Games whose name is known (custom games, Epic, Ubisoft) are matched to SteamGridDB by exact name.
> - Steam games SteamGridDB doesn't know by ID yet (new releases) get their name from the Steam store, so they no longer show as Unknown.
> - The list reloads automatically when the Xbox app adds or removes games while the widget is open.
> - Faster and lighter: game lookups are cached between sessions, covers are decoded at display size, store searches are cached, and "Fix my library" processes several games at once.
>
## Install
>
> **Option 1:** go to the [latest release](https://github.com/ykyritsis/steamgriddb-xbox/releases/latest), download the zip for your PC (**x64** for most PCs and the ROG Ally / Xbox Ally, **ARM64** for Snapdragon), unzip it and double-click **`Install.cmd`**.
>
> **Option 2:** paste this into PowerShell (it picks the right download for your PC automatically):
>
> ```powershell
> irm https://raw.githubusercontent.com/ykyritsis/steamgriddb-xbox/master/install.ps1 | iex
> ```
>
> The installer asks for administrator permission **once** to trust the package's signing certificate, then installs the widget and anything it needs. Run it again any time to update; updates don't need the admin prompt.
>
> **After installing:**
> 1. Turn on **SteamGridDB for Xbox (Community)** under *Settings > Privacy & security > File system* (the installer opens this page).
> 2. Open Game Bar (**Win+G**), open the widget menu and pick **SteamGridDB (Community)**.
> 3. Click the gear icon and paste your free [SteamGridDB API key](https://www.steamgriddb.com/profile/preferences/api).
>
> This build is standalone: you don't need the Microsoft Store version. If you have it, you can keep it or uninstall it.


Xbox Game Bar widget to customise Xbox PC app game artwork with images from [SteamGridDB](https://www.steamgriddb.com/). Replace wrong, low-resolution or missing images for games from third-party stores (Steam, GOG, Epic Games Store, EA App, Ubisoft Connect or manually added games) with high-quality artwork submitted by community.

Automatically detects correct artwork for Steam games and most popular GOG titles through SteamGridDB's platform ID matching. For all other games use the built-in search feature to manually select the correct game - game names will be pre-populated where possible by using other external APIs.

Battle.net is unlikely to be ever supported because the Xbox app does not store images for Battle.net games in the same way as the other stores, and games from Battle.net usually already have high-quality cover art in the Xbox app.

The widget requires user to enable File system access under ***Settings > Privacy & security > File system > Let apps access your file system***, because by default it runs in a sandboxed environment where it is not allowed to access data of the other apps such as the Xbox app - this is the only way to enable such functionality. The only files being accessed are the Xbox app images downloaded for games installed from the third-party libraries.

### Currently known issues and/or limitations
- Demos are not supported for automatic matching, even from Steam - their ID is different from the main game, but their artwork can still be changed with manual search.
- The widget is specifically looking for square grids (512x512 or 1024x1024) or icons (which are always square), because the Xbox app is designed to show square artwork. That is why results from SteamGridDB are filtered and do not show all available images.
- Freshly uploaded SteamGridDB artwork might not show up in the widget immediately due to SteamGridDB API caching.
- Currently, there is no source to resolve names for games from the EA App.
- Sometimes the Xbox app leaves behind manifest entries for removed games, causing them to appear in the widget. Conversely, some installed games can be missing from the manifest and not show up in the widget. To solve this, delete the `ThirdPartyLibraries` folder (located in `C:\Users\{yourWindowsUsername}\AppData\Local\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\LocalState\`) or the manifest files (in `ThirdPartyLibraries` subfolders for corresponding stores) — the Xbox app will recreate them correctly.
- Only first 50 square grids and first 50 icons are loaded from SteamGridDB (paging is not implemented yet).

If you are building the project yourself, you will need your own SteamGridDB API key that can be obtained [here](https://www.steamgriddb.com/profile/preferences/api).

Powered by SteamGridDB API and GOG API. Not affiliated with SteamGridDB, Xbox, Steam, GOG, Epic Games, Electronic Arts, Ubisoft or their subsidiaries. All trademarks are property of their respective owners.

Credit to https://github.com/nachoaldamav/items-tracker for Epic Games Store database.

Credit to https://github.com/Haoose/UPLAY_GAME_ID for Ubisoft Connect database. 
