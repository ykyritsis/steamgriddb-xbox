using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Windows.Data.Json;
using Windows.Storage;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;

using SteamGridDB.Xbox.Models;
using SteamGridDB.Xbox.Services;
using SteamGridDB.Xbox.Services.Artwork;
using SteamGridDB.Xbox.Services.SteamGridDB;
using SteamGridDB.Xbox.Services.SteamGridDB.Models;
using SteamGridDB.Xbox.Services.Steam;
using SteamGridDB.Xbox.Services.Stores;
using SteamGridDB.Xbox.Services.Xbox;

namespace SteamGridDB.Xbox
{
    // Loading the Xbox app library: store manifests, custom apps and watching for changes.
    public sealed partial class PrimaryWidget
    {
        private async Task<StorageFolder> GetThirdPartyLibrariesFolderAsync()
        {
            try
            {
                // Try to get folder directly with broadFileSystemAccess permission
                return await StorageFolder.GetFolderFromPathAsync(thirdPartyLibrariesPath);
            }
            catch (UnauthorizedAccessException)
            {
                // Access denied - user needs to grant permission in Windows Settings
                return null;
            }
            catch (FileNotFoundException)
            {
                // Directory doesn't exist
                throw new DirectoryNotFoundException($"ThirdPartyLibraries folder not found at: {thirdPartyLibrariesPath}");
            }
            catch
            {
                // Other error
                return null;
            }
        }

        private async Task LoadGameEntriesAsync()
        {
            // Refreshing while a load is in progress used to interleave two loads into one list
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;

            try
            {
                GameEntries.Clear();
                RefreshDisplayedEntries();
                StatusText.Text = "Attempting to access ThirdPartyLibraries...";
                InstructionsPanel.Visibility = Visibility.Collapsed;
                GameEntriesListView.Visibility = Visibility.Visible;

                StorageFolder thirdPartyFolder;

                try
                {
                    thirdPartyFolder = await GetThirdPartyLibrariesFolderAsync();
                }
                catch (DirectoryNotFoundException)
                {
                    StatusText.Text = "ThirdPartyLibraries folder was not found. Make sure games are added to the Xbox app.";
                    GameEntriesListView.Visibility = Visibility.Collapsed;

                    return;
                }

                if (thirdPartyFolder == null)
                {
                    StatusText.Text = "Access denied. Please grant file system permission.";
                    InstructionsPanel.Visibility = Visibility.Visible;
                    GameEntriesListView.Visibility = Visibility.Collapsed;

                    return;
                }

                // Get all subdirectories (as a copy - the live listing changes when the Xbox app writes files)
                List<StorageFolder> folders = await StorageSnapshot.GetAsync(async () => await thirdPartyFolder.GetFoldersAsync());

                string directoryNames = string.Join(", ", folders.Select(f => f.Name));
                StatusText.Text = $"Found {folders.Count} director{(folders.Count == 1 ? "y" : "ies")} ({directoryNames}). Reading manifests...";

                List<GameEntry> tmpGameList = new List<GameEntry>();
                int unreadableEntries = 0;

                foreach (StorageFolder folder in folders)
                {
                    GamePlatform platform = GamePlatformHelper.FromXboxDirectory(folder.Name);

                    if (platform == GamePlatform.BattleNet)
                    {
                        // Skip Battle.net folder as it is not currently supported - Xbox app does not store images here
                        continue;
                    }

                    List<KeyValuePair<string, IJsonValue>> gameCacheEntries;

                    try
                    {
                        JsonObject gameCache = await ReadGameCacheAsync(folder);

                        if (gameCache == null)
                        {
                            continue;
                        }

                        gameCacheEntries = StorageSnapshot.Copy(gameCache);
                    }
                    catch (Exception ex)
                    {
                        // One store's manifest must never stop the others from loading
                        System.Diagnostics.Debug.WriteLine($"Could not read the {folder.Name} manifest: {ex.Message}");

                        continue;
                    }

                    // Every entry is processed on its own: previously one unexpected error (a locked image file,
                    // a number where a string was expected...) silently dropped every remaining game in that store,
                    // which is why the number of games found changed from one refresh to the next.
                    foreach (KeyValuePair<string, IJsonValue> entry in gameCacheEntries)
                    {
                        if (entry.Key == "version" || entry.Value.ValueType != JsonValueType.Object)
                        {
                            continue;
                        }

                        try
                        {
                            GameEntry game = await CreateGameEntryAsync(folder, platform, entry.Value.GetObject());

                            if (game != null)
                            {
                                tmpGameList.Add(game);
                            }
                        }
                        catch (Exception ex)
                        {
                            unreadableEntries++;

                            System.Diagnostics.Debug.WriteLine($"Error reading entry {entry.Key} in {folder.Name}: {ex.Message}");
                        }
                    }
                }

                try
                {
                    using (SteamGridDbClient sgdbClient = CreateSteamGridDbClient())
                    {
                        await ResolveGameNamesAsync(sgdbClient, tmpGameList);
                    }
                }
                catch (Exception ex)
                {
                    // Names are a nice-to-have: always show the games that were found
                    System.Diagnostics.Debug.WriteLine($"Could not resolve game names: {ex.Message}");
                }

                // Custom apps from the Xbox app's "Apps" tab: their names are already known, so no lookups
                List<GameEntry> apps = await LoadCustomAppsAsync();

                // Sort games alphabetically by name, with "Unknown" at the end, then apps
                List<GameEntry> sortedGames = tmpGameList
                    .OrderBy(g => g.Name == unknownName ? 1 : 0)
                    .ThenBy(g => g.Name)
                    .Concat(apps.OrderBy(a => a.Name))
                    .ToList();

                foreach (GameEntry game in sortedGames)
                {
                    GameEntries.Add(game);
                }

                RefreshDisplayedEntries();

                int gameCount = tmpGameList.Count;
                string status = $"Found {gameCount} game{(gameCount == 1 ? string.Empty : "s")}" +
                    (apps.Count > 0 ? $" and {apps.Count} app{(apps.Count == 1 ? string.Empty : "s")}" : string.Empty);

                if (unreadableEntries > 0)
                {
                    status += $", {unreadableEntries} manifest entr{(unreadableEntries == 1 ? "y" : "ies")} could not be read";
                }

                if (string.IsNullOrEmpty(AppSettings.SteamGridDbApiKey))
                {
                    status += ". Add a SteamGridDB API key in Settings for automatic matching";
                }

                StatusText.Text = status;

                await WatchManifestsAsync(thirdPartyFolder);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;

                // Shows the empty-tab hint now that loading has finished
                RefreshDisplayedEntries();

                // Pick up a library change that arrived while busy (no-op when nothing changed)
                ReloadIfPending();
            }
        }

        /// <summary>
        /// Remembers the state of the manifests just loaded and starts watching them, so games the Xbox app adds or
        /// removes while the widget is open show up without a manual refresh.
        /// </summary>
        private async Task WatchManifestsAsync(StorageFolder thirdPartyFolder)
        {
            try
            {
                loadedManifestSignature = await GetManifestSignatureAsync(thirdPartyFolder);

                if (manifestQuery != null)
                {
                    return;
                }

                QueryOptions options = new QueryOptions(CommonFileQuery.DefaultQuery, new[] { manifestFileExtension })
                {
                    FolderDepth = FolderDepth.Deep,
                    IndexerOption = IndexerOption.DoNotUseIndexer
                };

                manifestQuery = thirdPartyFolder.CreateFileQueryWithOptions(options);
                manifestQuery.ContentsChanged += ManifestQuery_ContentsChanged;

                // Change notifications only start once the query has been run
                await manifestQuery.GetFilesAsync();

                // The custom apps list is in a separate folder, so it needs its own watcher
                try
                {
                    StorageFolder appsFolder = await CustomLibraryWriter.GetAppsFolderAsync();

                    appsManifestQuery = appsFolder.CreateFileQueryWithOptions(new QueryOptions(CommonFileQuery.DefaultQuery, new[] { manifestFileExtension })
                    {
                        FolderDepth = FolderDepth.Shallow,
                        IndexerOption = IndexerOption.DoNotUseIndexer
                    });
                    appsManifestQuery.ContentsChanged += ManifestQuery_ContentsChanged;

                    await appsManifestQuery.GetFilesAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not watch the custom apps list: {ex.Message}");
                }

                manifestDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                manifestDebounceTimer.Tick += ManifestDebounceTimer_Tick;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not watch manifests: {ex.Message}");
            }
        }

        private void ManifestQuery_ContentsChanged(IStorageQueryResultBase sender, object args)
        {
            // Raised on a background thread, often several times per write - restart the debounce timer on the UI thread
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                manifestDebounceTimer?.Stop();
                manifestDebounceTimer?.Start();
            });
        }

        private async void ManifestDebounceTimer_Tick(object sender, object e)
        {
            manifestDebounceTimer.Stop();

            await ReloadIfManifestsChangedAsync();
        }

        /// <summary>
        /// Reloads the library if a manifest changed since the last load. Waits while busy or while a panel is open.
        /// </summary>
        private async Task ReloadIfManifestsChangedAsync()
        {
            if (IsBusy || GridSelectionPanel.Visibility == Visibility.Visible || GameSearchPanel.Visibility == Visibility.Visible)
            {
                manifestReloadPending = true;

                return;
            }

            manifestReloadPending = false;

            try
            {
                StorageFolder thirdPartyFolder = await GetThirdPartyLibrariesFolderAsync();

                // The widget's own artwork writes also raise change events; only manifest changes matter
                if (thirdPartyFolder == null || await GetManifestSignatureAsync(thirdPartyFolder) == loadedManifestSignature)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not check manifests: {ex.Message}");

                return;
            }

            await LoadGameEntriesAsync();
        }

        private async void ReloadIfPending()
        {
            if (manifestReloadPending)
            {
                await ReloadIfManifestsChangedAsync();
            }
        }

        /// <summary>
        /// Size and modification time of every store manifest, as one comparable string.
        /// </summary>
        private static async Task<string> GetManifestSignatureAsync(StorageFolder thirdPartyFolder)
        {
            List<string> parts = new List<string>();

            foreach (StorageFolder folder in await StorageSnapshot.GetAsync(async () => await thirdPartyFolder.GetFoldersAsync()))
            {
                if (await folder.TryGetItemAsync($"{folder.Name}{manifestFileExtension}") is StorageFile manifestFile)
                {
                    Windows.Storage.FileProperties.BasicProperties properties = await manifestFile.GetBasicPropertiesAsync();

                    parts.Add($"{folder.Name}:{properties.Size}:{properties.DateModified.UtcTicks}");
                }
            }

            // The custom apps list lives outside ThirdPartyLibraries
            try
            {
                StorageFolder appsFolder = await CustomLibraryWriter.GetAppsFolderAsync();

                if (await appsFolder.TryGetItemAsync($"{appsFolder.Name}{manifestFileExtension}") is StorageFile appsManifest)
                {
                    Windows.Storage.FileProperties.BasicProperties properties = await appsManifest.GetBasicPropertiesAsync();

                    parts.Add($"apps:{properties.Size}:{properties.DateModified.UtcTicks}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not check the custom apps list: {ex.Message}");
            }

            return string.Join("|", parts);
        }

        /// <summary>
        /// Reads the custom apps the user added in the Xbox app's "Apps" tab. They use the custom games format, but live in
        /// a separate list. The built-in launcher tiles on that tab ship inside the Xbox app's package and cannot be changed.
        /// </summary>
        private async Task<List<GameEntry>> LoadCustomAppsAsync()
        {
            List<GameEntry> apps = new List<GameEntry>();

            try
            {
                StorageFolder appsFolder = await CustomLibraryWriter.GetAppsFolderAsync();
                JsonObject appCache = await ReadGameCacheAsync(appsFolder);

                if (appCache == null)
                {
                    return apps;
                }

                foreach (KeyValuePair<string, IJsonValue> entry in StorageSnapshot.Copy(appCache))
                {
                    if (entry.Key == "version" || entry.Value.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    try
                    {
                        GameEntry app = await CreateGameEntryAsync(appsFolder, GamePlatform.App, entry.Value.GetObject());

                        if (app != null)
                        {
                            apps.Add(app);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error reading custom app {entry.Key}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Older Xbox app versions have no apps list
                System.Diagnostics.Debug.WriteLine($"Could not read custom apps: {ex.Message}");
            }

            return apps;
        }

        /// <summary>
        /// Reads a store's manifest and returns its gameCache object, retrying when the Xbox app is in the middle of writing it.
        /// </summary>
        private static async Task<JsonObject> ReadGameCacheAsync(StorageFolder folder)
        {
            string manifestFileName = $"{folder.Name}{manifestFileExtension}";

            for (int attempt = 1; attempt <= manifestReadAttempts; attempt++)
            {
                try
                {
                    if (!(await folder.TryGetItemAsync(manifestFileName) is StorageFile manifestFile))
                    {
                        // Manifest file doesn't exist in this directory
                        return null;
                    }

                    string jsonContent = await FileIO.ReadTextAsync(manifestFile);

                    if (string.IsNullOrWhiteSpace(jsonContent))
                    {
                        // The Xbox app leaves empty manifests for stores with no games
                        return null;
                    }

                    if (JsonObject.TryParse(jsonContent, out JsonObject root))
                    {
                        return root.TryGetValue("gameCache", out IJsonValue gameCache) && gameCache.ValueType == JsonValueType.Object
                            ? gameCache.GetObject()
                            : null;
                    }

                    // Invalid JSON usually means the file was caught half-written
                    System.Diagnostics.Debug.WriteLine($"Manifest {manifestFileName} is not valid JSON (attempt {attempt})");
                }
                catch (Exception ex)
                {
                    // Typically a sharing violation while the Xbox app writes the file
                    System.Diagnostics.Debug.WriteLine($"Could not read {manifestFileName} (attempt {attempt}): {ex.Message}");
                }

                await Task.Delay(300 * attempt);
            }

            return null;
        }

        /// <summary>
        /// Builds a game entry from one manifest entry. Game names are resolved separately.
        /// </summary>
        private async Task<GameEntry> CreateGameEntryAsync(StorageFolder folder, GamePlatform platform, JsonObject entryObject)
        {
            // Get the ID from the "id" property (not from the key)
            string entryId = GetJsonString(entryObject, "id");

            if (string.IsNullOrEmpty(entryId))
            {
                return null;
            }

            // addedDate is normally a string, but accept a number too
            long.TryParse(GetJsonString(entryObject, "addedDate"), out long timestamp);

            string imageFilePath = null;
            StorageFolder imageFolder = null;

            if (GamePlatformHelper.IsCustomLibrary(platform)) // Custom games and apps store the full image path
            {
                imageFilePath = GetJsonString(entryObject, "imagePath");

                if (!string.IsNullOrEmpty(imageFilePath))
                {
                    try
                    {
                        imageFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(imageFilePath));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Image folder for custom game {entryId} is not accessible: {ex.Message}");
                    }
                }
            }
            else // Image filename is based on ID
            {
                imageFilePath = Path.Combine(thirdPartyLibrariesPath, folder.Name, entryId.Replace(":", "_") + imageExtension);
                imageFolder = folder;
            }

            string imageFileName = string.IsNullOrEmpty(imageFilePath) ? "No image path" : Path.GetFileName(imageFilePath);
            BitmapImage image = null;
            bool hasBackup = false;

            if (imageFolder != null)
            {
                hasBackup = await imageFolder.TryGetItemAsync(GetSiblingFileName(imageFileName, backupImageExtension)) != null;

                if (await imageFolder.TryGetItemAsync(imageFileName) is StorageFile imageFile)
                {
                    try
                    {
                        image = await LoadBitmapAsync(imageFile);
                    }
                    catch (Exception ex)
                    {
                        // Locked or corrupt image - still list the game so it can be fixed
                        System.Diagnostics.Debug.WriteLine($"Could not load image {imageFileName}: {ex.Message}");
                    }
                }
                else
                {
                    imageFileName = "Not found";
                }
            }

            string gameName = unknownName;
            string xboxPlatformId;
            string externalPlatformId;

            if (GamePlatformHelper.IsCustomLibrary(platform))
            {
                gameName = GetJsonString(entryObject, "title") ?? unknownName;
                xboxPlatformId = entryId;
                externalPlatformId = Path.Combine(GetJsonString(entryObject, "installLocation") ?? string.Empty, GetJsonString(entryObject, "executableName") ?? string.Empty);
            }
            else
            {
                xboxPlatformId = entryId.Substring(entryId.IndexOf(':') + 1);
                externalPlatformId = xboxPlatformId;

                if (platform == GamePlatform.Epic)
                {
                    // For Epic, entryId format is "epic:namespace:ID"
                    string[] parts = entryId.Split(':');

                    if (parts.Length >= 3)
                    {
                        externalPlatformId = parts[2];
                    }
                }
            }

            return new GameEntry
            {
                Name = gameName,
                XboxPlatformId = xboxPlatformId,
                ExternalPlatformId = externalPlatformId,
                ImageFileName = imageFileName,
                ImageFilePath = imageFilePath,
                ImageFolder = imageFolder,
                Platform = platform,
                AddedDate = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime,
                Image = image,
                HasBackup = hasBackup
            };
        }
    }
}
