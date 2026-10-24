using System;
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
    /// <summary>
    /// Primary widget page that loads and displays Xbox app third-party games.
    /// </summary>
    /// <remarks>
    /// All work runs on the UI thread's synchronisation context (awaits resume there), so UI elements and the
    /// static caches below can be used directly without dispatching.
    /// </remarks>
    public sealed partial class PrimaryWidget : Page, INotifyPropertyChanged
    {
        public ObservableCollection<GameEntry> GameEntries
        {
            get; set;
        }

        private readonly string thirdPartyLibrariesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"AppData\Local\Packages\Microsoft.GamingApp_8wekyb3d8bbwe\LocalState\ThirdPartyLibraries");
        private const string unknownName = "Unknown";
        private const string imageExtension = ".png";
        private const string backupImageExtension = ".bak";
        private const string newImageExtension = ".new";
        private const string manifestFileExtension = ".manifest";
        private const string steamGridDbSource = "SteamGridDB";
        private const int nameLookupConcurrency = 4;
        private const int fixLibraryConcurrency = 3;
        private const int thumbnailDecodeSize = 80;
        private const int manifestReadAttempts = 3;
        private const string restartHint = "switch tabs in the Xbox app (e.g. to Home and back to Library) to see the new artwork";

        private static Dictionary<string, string> ubisoftGameLookupCache = null;
        private static readonly Dictionary<string, string> gogNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> epicNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient sharedHttpClient = new HttpClient();

        private Button lastFocusedButton;

        // Set while the library is loading or a bulk operation runs, so they cannot overlap
        private bool isBusy;

        // Incremented every time the artwork panel opens or closes, so late results from a previous game are discarded
        private int gridPanelRequestId;

        // Watches the Xbox app's manifests so newly detected games appear without a manual refresh
        private StorageFileQueryResult manifestQuery;
        private DispatcherTimer manifestDebounceTimer;
        private string loadedManifestSignature;
        private bool manifestReloadPending;

        private GameEntry currentSelectedGame;
        public GameEntry CurrentSelectedGame
        {
            get => currentSelectedGame;
            set
            {
                if (currentSelectedGame != value)
                {
                    currentSelectedGame = value;
                    OnPropertyChanged(nameof(CurrentSelectedGame));
                }
            }
        }

        private string gridPanelHeaderText;
        public string GridPanelHeaderText
        {
            get => gridPanelHeaderText;
            set
            {
                if (gridPanelHeaderText != value)
                {
                    gridPanelHeaderText = value;
                    OnPropertyChanged(nameof(GridPanelHeaderText));
                }
            }
        }

        private string searchPanelHeaderText;
        public string SearchPanelHeaderText
        {
            get => searchPanelHeaderText;
            set
            {
                if (searchPanelHeaderText != value)
                {
                    searchPanelHeaderText = value;
                    OnPropertyChanged(nameof(SearchPanelHeaderText));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public PrimaryWidget()
        {
            InitializeComponent();
            GameEntries = new ObservableCollection<GameEntry>();
            Loaded += PrimaryWidget_Loaded;
        }

        private async void PrimaryWidget_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadGameEntriesAsync();

            // Set default focus to Fix my library button for controller navigation
            FixLibraryButton.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// Creates a SteamGridDB client, or returns null when no API key has been configured.
        /// </summary>
        private static SteamGridDbClient CreateSteamGridDbClient()
        {
            string apiKey = AppSettings.SteamGridDbApiKey;

            return string.IsNullOrEmpty(apiKey) ? null : new SteamGridDbClient(apiKey);
        }

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
            if (isBusy)
            {
                return;
            }

            isBusy = true;

            try
            {
                GameEntries.Clear();
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

                // Get all subdirectories
                IReadOnlyList<StorageFolder> folders = await thirdPartyFolder.GetFoldersAsync();

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

                    JsonObject gameCache = await ReadGameCacheAsync(folder);

                    if (gameCache == null)
                    {
                        continue;
                    }

                    // Every entry is processed on its own: previously one unexpected error (a locked image file,
                    // a number where a string was expected...) silently dropped every remaining game in that store,
                    // which is why the number of games found changed from one refresh to the next.
                    foreach (KeyValuePair<string, IJsonValue> entry in gameCache)
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

                using (SteamGridDbClient sgdbClient = CreateSteamGridDbClient())
                {
                    await ResolveGameNamesAsync(sgdbClient, tmpGameList);
                }

                // Sort games alphabetically by name, with "Unknown" at the end
                List<GameEntry> sortedGames = tmpGameList
                    .OrderBy(g => g.Name == unknownName ? 1 : 0)
                    .ThenBy(g => g.Name)
                    .ToList();

                foreach (GameEntry game in sortedGames)
                {
                    GameEntries.Add(game);
                }

                string status = $"Found {GameEntries.Count} game{(GameEntries.Count == 1 ? string.Empty : "s")}";

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
                isBusy = false;

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
            if (isBusy || GridSelectionPanel.Visibility == Visibility.Visible || GameSearchPanel.Visibility == Visibility.Visible)
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

            foreach (StorageFolder folder in await thirdPartyFolder.GetFoldersAsync())
            {
                if (await folder.TryGetItemAsync($"{folder.Name}{manifestFileExtension}") is StorageFile manifestFile)
                {
                    Windows.Storage.FileProperties.BasicProperties properties = await manifestFile.GetBasicPropertiesAsync();

                    parts.Add($"{folder.Name}:{properties.Size}:{properties.DateModified.UtcTicks}");
                }
            }

            return string.Join("|", parts);
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

            if (platform == GamePlatform.Custom) // Custom contains full path for the image filename
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

            if (platform == GamePlatform.Custom)
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

        /// <summary>
        /// Resolves names and SteamGridDB matches for all games, a few at a time.
        /// </summary>
        private async Task ResolveGameNamesAsync(SteamGridDbClient sgdbClient, List<GameEntry> games)
        {
            int completed = 0;

            using (SemaphoreSlim throttle = new SemaphoreSlim(nameLookupConcurrency))
            {
                IEnumerable<Task> lookups = games.Select(async game =>
                {
                    string cacheKey = GameInfoCache.GetKey(game.Platform.ToString(), game.ExternalPlatformId);
                    CachedGameInfo cached = await GameInfoCache.TryGetAsync(cacheKey, sgdbClient != null);

                    if (cached != null)
                    {
                        // Custom games keep the title the user gave them in the Xbox app
                        if (game.Platform != GamePlatform.Custom && !string.IsNullOrEmpty(cached.Name))
                        {
                            game.Name = cached.Name;
                        }

                        game.SteamGridDbGameId = cached.SteamGridDbGameId;
                        game.HasSteamGridDBMatch = cached.HasSteamGridDBMatch;
                        completed++;

                        return;
                    }

                    await throttle.WaitAsync();

                    try
                    {
                        await ResolveGameNameAsync(sgdbClient, game);

                        // Nothing resolved usually means the network was unavailable - try again next time
                        if (game.Name != unknownName || game.HasSteamGridDBMatch)
                        {
                            await GameInfoCache.SetAsync(cacheKey, new CachedGameInfo
                            {
                                Name = game.Name == unknownName ? null : game.Name,
                                SteamGridDbGameId = game.SteamGridDbGameId,
                                HasSteamGridDBMatch = game.HasSteamGridDBMatch,
                                ResolvedWithSteamGridDb = sgdbClient != null,
                                ResolvedAt = DateTimeOffset.UtcNow
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail - game name is optional, default is "Unknown"
                        System.Diagnostics.Debug.WriteLine($"Could not resolve name for {game.Platform} {game.ExternalPlatformId}: {ex.Message}");
                    }
                    finally
                    {
                        throttle.Release();
                        completed++;
                        StatusText.Text = $"Looking up games ({completed}/{games.Count})...";
                    }
                });

                await Task.WhenAll(lookups);
            }

            await GameInfoCache.SaveAsync();
        }

        private async Task ResolveGameNameAsync(SteamGridDbClient sgdbClient, GameEntry game)
        {
            string platformString = GamePlatformHelper.GamePlatformToSGDBApiString(game.Platform);

            // Try to fetch game name from SteamGridDB API
            if (sgdbClient != null && !string.IsNullOrEmpty(platformString))
            {
                try
                {
                    SteamGridDbGame gameInfo = await sgdbClient.GetGameByPlatformIdAsync(platformString, game.ExternalPlatformId);

                    if (gameInfo != null && !string.IsNullOrEmpty(gameInfo.Name))
                    {
                        game.Name = gameInfo.Name;
                        game.SteamGridDbGameId = gameInfo.Id;
                        game.HasSteamGridDBMatch = true;

                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not fetch game name for {game.ExternalPlatformId} from SteamGridDB: {ex.Message}");
                }
            }

            if (game.Platform == GamePlatform.Steam)
            {
                // New releases are often not linked to their Steam ID on SteamGridDB yet
                string steamName = await GetSteamGameNameAsync(game.ExternalPlatformId);

                if (!string.IsNullOrEmpty(steamName))
                {
                    game.Name = steamName;
                }
            }
            else if (game.Platform == GamePlatform.GOG)
            {
                if (!gogNameCache.TryGetValue(game.ExternalPlatformId, out string gogName) || string.IsNullOrEmpty(gogName))
                {
                    gogName = await GetGogGameNameAsync(game.ExternalPlatformId);

                    if (!string.IsNullOrEmpty(gogName))
                    {
                        gogNameCache[game.ExternalPlatformId] = gogName;
                    }
                }

                if (!string.IsNullOrEmpty(gogName))
                {
                    game.Name = gogName;
                }
            }
            else if (game.Platform == GamePlatform.Epic)
            {
                if (!epicNameCache.TryGetValue(game.ExternalPlatformId, out string epicName) || string.IsNullOrEmpty(epicName))
                {
                    epicName = await GetEpicGameNameAsync(game.ExternalPlatformId);

                    if (!string.IsNullOrEmpty(epicName))
                    {
                        epicNameCache[game.ExternalPlatformId] = epicName;
                    }
                }

                if (!string.IsNullOrEmpty(epicName))
                {
                    game.Name = epicName;
                }
            }
            else if (game.Platform == GamePlatform.Ubisoft)
            {
                string ubisoftName = await GetUbisoftGameNameAsync(game.ExternalPlatformId);

                if (!string.IsNullOrEmpty(ubisoftName))
                {
                    game.Name = ubisoftName;
                }
            }
            else if (game.Platform == GamePlatform.EA)
            {
                // TODO: Implement EA App name fetching if possible
            }

            // A known name with an exact SteamGridDB match is as good as a platform ID match (custom games, Epic, Ubisoft...)
            if (sgdbClient != null && game.Name != unknownName)
            {
                SteamGridDbGame exactMatch = await sgdbClient.FindGameByExactNameAsync(game.Name);

                if (exactMatch != null)
                {
                    game.SteamGridDbGameId = exactMatch.Id;
                    game.HasSteamGridDBMatch = true;
                }
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadGameEntriesAsync();
        }

        /// <summary>
        /// Handles fix library button click to automatically download artwork for all eligible games.
        /// </summary>
        private async void FixLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                StatusText.Text = "Please wait for the current operation to finish";

                return;
            }

            ContentDialog confirmDialog = CreateDialog(
                "Fix my library",
                "This will automatically download the highest-scored artwork from SteamGridDB for all games that have a SteamGridDB match and have not been manually modified yet." +
                (AppSettings.UseStoreSources ? " Games without SteamGridDB artwork will use Xbox Store, PlayStation Store or IGN cover art when the title matches exactly." : string.Empty) + "\n\n" +
                "Original images will be backed up and can be restored later.\n\n" +
                "Do you want to continue?",
                "Fix my library");

            ContentDialogResult result = await confirmDialog.ShowAsync();

            // Only proceed if user clicked the primary button
            if (result == ContentDialogResult.Primary)
            {
                await FixLibraryAsync();
            }
        }

        private async void RestoreChangesButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                StatusText.Text = "Please wait for the current operation to finish";

                return;
            }

            ContentDialog confirmDialog = CreateDialog(
                "Restore my changes",
                "This will restore all previously customised artwork (useful if your changes were reset by the Xbox app).\n\n" +
                "Do you want to continue?",
                "Restore my changes");

            ContentDialogResult result = await confirmDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                await RestoreAllChangesAsync();
            }
        }

        private ContentDialog CreateDialog(string title, object content, string primaryButtonText)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = "Cancel",
                RequestedTheme = ElementTheme.Dark,
                Style = Resources["DarkContentDialogStyle"] as Style,
                PrimaryButtonStyle = Resources["ContentDialogButtonStyle"] as Style,
                CloseButtonStyle = Resources["ContentDialogButtonStyle"] as Style
            };

            // Set XamlRoot for proper dialog display
            if (Windows.Foundation.Metadata.ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
            {
                dialog.XamlRoot = Content.XamlRoot;
            }

            return dialog;
        }

        /// <summary>
        /// Shows the settings dialog (SteamGridDB API key, artwork protection and extra artwork sources).
        /// </summary>
        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            string currentApiKey = AppSettings.SteamGridDbApiKey ?? string.Empty;
            bool currentLock = AppSettings.LockArtwork;

            TextBox apiKeyBox = new TextBox
            {
                Header = "SteamGridDB API key",
                PlaceholderText = "From steamgriddb.com/profile/preferences/api",
                Text = currentApiKey
            };
            apiKeyBox.GotFocus += GameSearchBox_GotFocus;
            apiKeyBox.LostFocus += GameSearchBox_LostFocus;

            ToggleSwitch lockToggle = new ToggleSwitch
            {
                Header = "Protect customised artwork from being reset by the Xbox app",
                IsOn = currentLock
            };

            ToggleSwitch storeToggle = new ToggleSwitch
            {
                Header = "Also offer artwork from Xbox Store, PlayStation Store and IGN",
                IsOn = AppSettings.UseStoreSources
            };

            TextBox steamPathBox = new TextBox
            {
                Header = "Steam folder (only if Steam is not in Program Files)",
                PlaceholderText = SteamLibraryScanner.DefaultSteamPaths[0],
                Text = AppSettings.SteamPath ?? string.Empty
            };
            steamPathBox.GotFocus += GameSearchBox_GotFocus;
            steamPathBox.LostFocus += GameSearchBox_LostFocus;

            StackPanel panel = new StackPanel { Spacing = 16 };
            panel.Children.Add(apiKeyBox);
            panel.Children.Add(steamPathBox);
            panel.Children.Add(lockToggle);
            panel.Children.Add(storeToggle);
            panel.Children.Add(new TextBlock
            {
                Text = "Protected artwork is made read-only. Restoring a backup removes the protection for that game.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.7
            });

            ContentDialog dialog = CreateDialog("Settings", new ScrollViewer { Content = panel }, "Save");

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            AppSettings.SteamGridDbApiKey = apiKeyBox.Text;
            AppSettings.LockArtwork = lockToggle.IsOn;
            AppSettings.UseStoreSources = storeToggle.IsOn;
            AppSettings.SteamPath = steamPathBox.Text;

            if (currentLock != lockToggle.IsOn)
            {
                await ApplyArtworkLockAsync(lockToggle.IsOn);
            }

            if ((apiKeyBox.Text ?? string.Empty).Trim() != currentApiKey)
            {
                // Matches made with a different key (or none) may be incomplete
                await GameInfoCache.ClearAsync();
                await LoadGameEntriesAsync();
            }
            else
            {
                StatusText.Text = "Settings saved";
            }
        }

        /// <summary>
        /// Locks or unlocks every customised image (the ones with a saved .new copy).
        /// </summary>
        private async Task ApplyArtworkLockAsync(bool locked)
        {
            int count = 0;

            foreach (GameEntry game in GameEntries.Where(g => g.ImageFolder != null && !string.IsNullOrEmpty(g.ImageFilePath)).ToList())
            {
                string imageFileName = Path.GetFileName(game.ImageFilePath);

                if (await game.ImageFolder.TryGetItemAsync(GetSiblingFileName(imageFileName, newImageExtension)) == null)
                {
                    continue;
                }

                string imagePath = Path.Combine(game.ImageFolder.Path, imageFileName);

                if (locked ? ArtworkFileLock.Lock(imagePath) : ArtworkFileLock.Unlock(imagePath))
                {
                    count++;
                }
            }

            StatusText.Text = $"Settings saved, artwork protection {(locked ? "enabled" : "removed")} for {count} game{(count == 1 ? string.Empty : "s")}";
        }

        /// <summary>
        /// Finds Steam games that are installed but missing from the Xbox app and adds them as custom library entries
        /// that launch through Steam (steam://rungameid/...), with cover art.
        /// </summary>
        private async void AddMissingSteamGamesButton_Click(object sender, RoutedEventArgs e)
        {
            if (isBusy)
            {
                StatusText.Text = "Please wait for the current operation to finish";

                return;
            }

            isBusy = true;

            try
            {
                StatusText.Text = "Looking for installed Steam games missing from the Xbox app...";

                StorageFolder steamFolder = await SteamLibraryScanner.FindSteamFolderAsync(AppSettings.SteamPath);

                if (steamFolder == null)
                {
                    StatusText.Text = "Steam was not found - set its folder in Settings";

                    return;
                }

                List<InstalledSteamGame> installedGames = await SteamLibraryScanner.GetInstalledGamesAsync(steamFolder);
                HashSet<string> inXboxApp = new HashSet<string>(GameEntries.Where(g => g.Platform == GamePlatform.Steam).Select(g => g.ExternalPlatformId));
                List<string> existingLaunchArguments = await CustomLibraryWriter.GetExistingLaunchArgumentsAsync();

                List<InstalledSteamGame> missingGames = installedGames
                    .Where(g => !inXboxApp.Contains(g.AppId) && !existingLaunchArguments.Any(args => LaunchesSteamApp(args, g.AppId)))
                    .ToList();

                // The Xbox app hides third-party games that have no cover, which looks like they are missing too
                int withoutCover = GameEntries.Count(g => g.Platform == GamePlatform.Steam && g.Image == null);
                string coverHint = withoutCover > 0
                    ? $" {withoutCover} Steam game{(withoutCover == 1 ? " is" : "s are")} in the Xbox app but without a cover, which the Xbox app may hide - use Fix my library to add covers."
                    : string.Empty;

                // Games the Xbox app detected can still be hidden by it (e.g. EA SPORTS FC 27, even with a cover),
                // so offer those too - unticked, since most of them are visible
                List<InstalledSteamGame> detectedGames = installedGames
                    .Where(g => inXboxApp.Contains(g.AppId) && !existingLaunchArguments.Any(args => LaunchesSteamApp(args, g.AppId)))
                    .ToList();

                if (missingGames.Count == 0 && detectedGames.Count == 0)
                {
                    StatusText.Text = $"All {installedGames.Count} installed Steam games are already in the Xbox app.{coverHint}";

                    return;
                }

                // Let the user pick which games to add
                StackPanel list = new StackPanel { Spacing = 4 };

                if (missingGames.Count > 0)
                {
                    list.Children.Add(new TextBlock { Text = "Not in the Xbox app", FontWeight = Windows.UI.Text.FontWeights.SemiBold });

                    foreach (InstalledSteamGame game in missingGames)
                    {
                        list.Children.Add(new CheckBox { Content = game.Name, Tag = game, IsChecked = true });
                    }
                }

                if (detectedGames.Count > 0)
                {
                    list.Children.Add(new TextBlock
                    {
                        Text = "Detected by the Xbox app - tick any you can't see there",
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                        Margin = new Thickness(0, missingGames.Count > 0 ? 12 : 0, 0, 0)
                    });

                    foreach (InstalledSteamGame game in detectedGames)
                    {
                        list.Children.Add(new CheckBox { Content = game.Name, Tag = game, IsChecked = false });
                    }
                }

                StackPanel content = new StackPanel { Spacing = 12 };
                content.Children.Add(new TextBlock
                {
                    Text = "Ticked games will be added as custom games that launch through Steam, with cover art." +
                           (missingGames.Count == 0 ? " All installed Steam games are detected, but the Xbox app sometimes hides some of them." : string.Empty) +
                           $"{coverHint}\n\nThe Xbox app will be closed while its library is updated.",
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260 });

                if (await CreateDialog("Add missing Steam games", content, "Add to Xbox app").ShowAsync() != ContentDialogResult.Primary)
                {
                    StatusText.Text = "No games were added";

                    return;
                }

                List<InstalledSteamGame> selectedGames = list.Children.OfType<CheckBox>()
                    .Where(c => c.IsChecked == true)
                    .Select(c => (InstalledSteamGame)c.Tag)
                    .ToList();

                if (selectedGames.Count == 0)
                {
                    StatusText.Text = "No games were added";

                    return;
                }

                // Covers first, so the Xbox app has them as soon as it loads the new entries
                StorageFolder imagesFolder = await CustomLibraryWriter.GetImagesFolderAsync();
                List<CustomGameEntry> entries = new List<CustomGameEntry>();

                using (SteamGridDbClient client = CreateSteamGridDbClient())
                {
                    foreach (InstalledSteamGame game in selectedGames)
                    {
                        StatusText.Text = $"Finding cover art for {game.Name} ({entries.Count + 1}/{selectedGames.Count})...";

                        entries.Add(new CustomGameEntry
                        {
                            Title = game.Name,
                            InstallLocation = steamFolder.Path.TrimEnd('\\') + "\\",
                            ExecutableName = "steam.exe",
                            ExecutableCommandArgs = GetSteamLaunchUrl(game.AppId),
                            ImagePath = await SaveCustomCoverAsync(client, game, imagesFolder)
                        });
                    }
                }

                StatusText.Text = "Closing the Xbox app...";

                bool closedAutomatically = await CustomLibraryWriter.TryCloseXboxAppAsync();

                if (closedAutomatically)
                {
                    await Task.Delay(1500);
                }
                else
                {
                    ContentDialog closeDialog = CreateDialog(
                        "Close the Xbox app",
                        "The Xbox app could not be closed automatically. It saves over its library while running, so please close it completely " +
                        "(including from the system tray), then press Continue.",
                        "Continue");

                    if (await closeDialog.ShowAsync() != ContentDialogResult.Primary)
                    {
                        StatusText.Text = "No games were added";

                        return;
                    }
                }

                List<string> addedIds = await CustomLibraryWriter.AddEntriesAsync(entries);

                // In Xbox full screen experience Windows restarts the Xbox app within a second of closing it,
                // so it may already have loaded the old library - close it once more so the restart reads the new one
                if (closedAutomatically)
                {
                    await Task.Delay(1500);
                    await CustomLibraryWriter.TryCloseXboxAppAsync();
                }

                await Task.Delay(3000);

                int kept = await CustomLibraryWriter.CountEntriesAsync(addedIds);

                StatusText.Text = kept == addedIds.Count
                    ? $"Added {kept} Steam game{(kept == 1 ? string.Empty : "s")} to the Xbox app - open the Xbox app to see {(kept == 1 ? "it" : "them")}"
                    : $"The Xbox app overwrote the change ({kept} of {addedIds.Count} kept) - close it completely and try again";

                // The custom library changed, so reload to show the new entries here too
                manifestReloadPending = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not add Steam games: {ex.Message}";

                System.Diagnostics.Debug.WriteLine($"Error in AddMissingSteamGamesButton_Click: {ex.Message}");
            }
            finally
            {
                isBusy = false;

                ReloadIfPending();
            }
        }

        private static string GetSteamLaunchUrl(string appId) => $"steam://rungameid/{appId}";

        private static bool LaunchesSteamApp(string launchArguments, string appId)
        {
            return !string.IsNullOrEmpty(launchArguments) &&
                   launchArguments.Split(new[] { ' ', '"' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Any(a => string.Equals(a.TrimEnd('/'), GetSteamLaunchUrl(appId), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Downloads the best square cover for a Steam game into the Xbox app's custom images folder. Returns its path,
        /// or an empty string when nothing was found (the Xbox app requires the field, empty is allowed).
        /// </summary>
        private async Task<string> SaveCustomCoverAsync(SteamGridDbClient client, InstalledSteamGame game, StorageFolder imagesFolder)
        {
            try
            {
                string url = null;

                if (client != null)
                {
                    List<SteamGridDbGrid> grids = await client.GetSquareGridsByPlatformIdAsync("steam", game.AppId);

                    if (grids.Count == 0)
                    {
                        SteamGridDbGame byName = await client.FindGameByExactNameAsync(game.Name);

                        if (byName != null)
                        {
                            grids = await client.GetSquareGridsByGameIdAsync(byName.Id);
                        }
                    }

                    url = grids.OrderByDescending(g => g.Score).FirstOrDefault()?.Url;
                }

                if (url == null && AppSettings.UseStoreSources)
                {
                    url = (await StoreArtworkClient.FindExactMatchAsync(game.Name))?.Url;
                }

                if (url == null)
                {
                    return string.Empty;
                }

                IBuffer downloaded;

                using (HttpResponseMessage response = await sharedHttpClient.GetAsync(new Uri(url)))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return string.Empty;
                    }

                    downloaded = await response.Content.ReadAsBufferAsync();
                }

                IBuffer cover = await ArtworkImageConverter.ToSquareImageAsync(downloaded, false);
                StorageFile file = await imagesFolder.CreateFileAsync($"sgdbx-steam-{game.AppId}.png", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBufferAsync(file, cover);

                return file.Path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not get a cover for {game.Name}: {ex.Message}");

                return string.Empty;
            }
        }

        /// <summary>
        /// Automatically downloads the best artwork for games with a match and no backup.
        /// </summary>
        private async Task FixLibraryAsync()
        {
            isBusy = true;

            try
            {
                bool useStores = AppSettings.UseStoreSources;

                // Get eligible games: not modified yet, writable, and something to match on
                List<GameEntry> eligibleGames = GameEntries
                    .Where(g => !g.HasBackup && g.ImageFolder != null && (g.HasSteamGridDBMatch || (useStores && g.Name != unknownName)))
                    .ToList();

                if (eligibleGames.Count == 0)
                {
                    StatusText.Text = "No eligible artworks to fix (all games either were already modified or have no match)";

                    return;
                }

                StatusText.Text = "Fixing library artwork...";

                int successCount = 0;
                int notFoundCount = 0;
                int errorCount = 0;

                using (SteamGridDbClient client = CreateSteamGridDbClient())
                using (SemaphoreSlim throttle = new SemaphoreSlim(fixLibraryConcurrency))
                {
                    // A few games at a time: most of the time is spent waiting on downloads
                    await Task.WhenAll(eligibleGames.Select(async game =>
                    {
                        await throttle.WaitAsync();

                        try
                        {
                            StatusText.Text = $"Fixing {game.Name} ({successCount + notFoundCount + errorCount + 1}/{eligibleGames.Count})...";

                            string artworkUrl = await FindBestArtworkUrlAsync(client, game, useStores);

                            if (artworkUrl == null)
                            {
                                notFoundCount++;

                                System.Diagnostics.Debug.WriteLine($"No artwork found for {game.Name}");
                            }
                            else if (await DownloadAndReplaceImageCoreAsync(game, artworkUrl, false))
                            {
                                successCount++;
                            }
                            else
                            {
                                errorCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            errorCount++;

                            System.Diagnostics.Debug.WriteLine($"Error processing {game.Name}: {ex.Message}");
                        }
                        finally
                        {
                            throttle.Release();
                        }
                    }));
                }

                StatusText.Text = $"Fixing library is complete: {successCount} updated, {notFoundCount} had no artwork available, {errorCount} error{(errorCount == 1 ? string.Empty : "s")}" +
                    (successCount > 0 ? $" - {restartHint}" : string.Empty);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error fixing library: {ex.Message}";

                System.Diagnostics.Debug.WriteLine($"Error in FixLibraryAsync: {ex.Message}");
            }
            finally
            {
                isBusy = false;

                // Pick up a library change that arrived while busy (no-op when nothing changed)
                ReloadIfPending();
            }
        }

        /// <summary>
        /// Picks artwork automatically: highest-scored SteamGridDB grid, then icon, then exact-title store art.
        /// </summary>
        private async Task<string> FindBestArtworkUrlAsync(SteamGridDbClient client, GameEntry game, bool useStores)
        {
            if (client != null && game.HasSteamGridDBMatch)
            {
                List<SteamGridDbGrid> grids = await GetSquareGridsAsync(client, game);

                if (grids.Count > 0)
                {
                    return grids.OrderByDescending(g => g.Score).First().Url;
                }

                List<SteamGridDbGrid> icons = await GetSquareIconsAsync(client, game);

                if (icons.Count > 0)
                {
                    return icons.OrderByDescending(i => i.Score).First().Url;
                }
            }

            if (useStores && game.Name != unknownName)
            {
                StoreArtwork storeArtwork = await StoreArtworkClient.FindExactMatchAsync(game.Name);

                return storeArtwork?.Url;
            }

            return null;
        }

        /// <summary>
        /// Square grids for a game, by SteamGridDB game ID when known (works for every platform), otherwise by platform ID.
        /// </summary>
        private static async Task<List<SteamGridDbGrid>> GetSquareGridsAsync(SteamGridDbClient client, GameEntry game)
        {
            if (game.SteamGridDbGameId.HasValue)
            {
                return await client.GetSquareGridsByGameIdAsync(game.SteamGridDbGameId.Value);
            }

            string platformString = GamePlatformHelper.GamePlatformToSGDBApiString(game.Platform);

            // Use the external ID - for Epic the Xbox ID also contains the namespace, which SteamGridDB does not know
            return string.IsNullOrEmpty(platformString)
                ? new List<SteamGridDbGrid>()
                : await client.GetSquareGridsByPlatformIdAsync(platformString, game.ExternalPlatformId);
        }

        private static async Task<List<SteamGridDbGrid>> GetSquareIconsAsync(SteamGridDbClient client, GameEntry game)
        {
            if (game.SteamGridDbGameId.HasValue)
            {
                return await client.GetSquareIconsByGameIdAsync(game.SteamGridDbGameId.Value);
            }

            string platformString = GamePlatformHelper.GamePlatformToSGDBApiString(game.Platform);

            return string.IsNullOrEmpty(platformString)
                ? new List<SteamGridDbGrid>()
                : await client.GetSquareIconsByPlatformIdAsync(platformString, game.ExternalPlatformId);
        }

        /// <summary>
        /// Restores artwork customisation by using saved .new files to replace current images - for cases when customisation was overwritten externally, for example, by the Xbox app.
        /// </summary>
        private async Task RestoreAllChangesAsync()
        {
            isBusy = true;

            try
            {
                StatusText.Text = "Restoring customisations...";

                int successCount = 0;
                int noArtworkCount = 0;
                int errorCount = 0;

                foreach (GameEntry game in GameEntries.ToList())
                {
                    string imageFileName = Path.GetFileName(game.ImageFilePath ?? string.Empty);
                    string gameName = game.Name == unknownName ? imageFileName : game.Name;

                    try
                    {
                        StatusText.Text = $"Restoring {gameName} ({successCount + noArtworkCount + errorCount + 1}/{GameEntries.Count})...";

                        if (game.ImageFolder == null || string.IsNullOrEmpty(imageFileName) ||
                            !(await game.ImageFolder.TryGetItemAsync(GetSiblingFileName(imageFileName, newImageExtension)) is StorageFile newFile))
                        {
                            noArtworkCount++;
                            System.Diagnostics.Debug.WriteLine($"Skipping {gameName} for restoration: corresponding .new file not found");

                            continue;
                        }

                        IBuffer imageBytes = await FileIO.ReadBufferAsync(newFile);

                        await WriteArtworkFileAsync(game.ImageFolder, imageFileName, imageBytes);

                        game.Image = await CreateBitmapAsync(imageBytes);
                        game.ImageFileName = imageFileName;

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        errorCount++;

                        System.Diagnostics.Debug.WriteLine($"Error restoring changes for {gameName}: {ex.Message}");
                    }
                }

                if (successCount == 0 && errorCount == 0)
                {
                    StatusText.Text = "No changes found to restore";
                }
                else
                {
                    StatusText.Text = $"Restore complete: {successCount} restored, {noArtworkCount} had no artwork saved, {errorCount} error{(errorCount == 1 ? string.Empty : "s")}" +
                        (successCount > 0 ? $" - {restartHint}" : string.Empty);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error restoring changes: {ex.Message}";

                System.Diagnostics.Debug.WriteLine($"Error in RestoreAllChangesAsync: {ex.Message}");
            }
            finally
            {
                isBusy = false;

                // Pick up a library change that arrived while busy (no-op when nothing changed)
                ReloadIfPending();
            }
        }

        /// <summary>
        /// Downloads and replaces an image for a specific game.
        /// </summary>
        /// <param name="game">The game to update</param>
        /// <param name="imageUrl">The URL of the image to download</param>
        /// <param name="updateStatusText">Whether to update the main status text</param>
        /// <returns>True if successful, false otherwise</returns>
        private async Task<bool> DownloadAndReplaceImageCoreAsync(GameEntry game, string imageUrl, bool updateStatusText = true)
        {
            if (game?.ImageFolder == null || string.IsNullOrEmpty(game.ImageFilePath))
            {
                return false;
            }

            try
            {
                IBuffer downloadedBytes;

                using (HttpResponseMessage response = await sharedHttpClient.GetAsync(new Uri(imageUrl)))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine($"Download of {imageUrl} failed: {response.StatusCode}");

                        return false;
                    }

                    downloadedBytes = await response.Content.ReadAsBufferAsync();
                }

                // Generate the filenames
                string imageFileName = Path.GetFileName(game.ImageFilePath);
                string backupFileName = GetSiblingFileName(imageFileName, backupImageExtension);
                string newFileName = GetSiblingFileName(imageFileName, newImageExtension);
                string extension = Path.GetExtension(imageFileName).ToLowerInvariant();

                // Always write a real, square image in the format the file name says
                IBuffer imageBytes = await ArtworkImageConverter.ToSquareImageAsync(downloadedBytes, extension == ".jpg" || extension == ".jpeg");

                // Create backup of ORIGINAL image ONLY if backup doesn't already exist
                bool backupExists = await game.ImageFolder.TryGetItemAsync(backupFileName) != null;

                if (!backupExists && await game.ImageFolder.TryGetItemAsync(imageFileName) is StorageFile existingImageFile)
                {
                    // Backup the ORIGINAL image by copying to preserve it
                    IBuffer existingBuffer = await FileIO.ReadBufferAsync(existingImageFile);
                    StorageFile backupFile = await game.ImageFolder.CreateFileAsync(backupFileName, CreationCollisionOption.ReplaceExisting);

                    await FileIO.WriteBufferAsync(backupFile, existingBuffer);

                    backupExists = true;
                }

                // Save the new image (replaces current)
                await WriteArtworkFileAsync(game.ImageFolder, imageFileName, imageBytes);

                // Save a copy of the new image as .new file
                StorageFile newFile = await game.ImageFolder.CreateFileAsync(newFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBufferAsync(newFile, imageBytes);

                // Reload the image in the UI
                game.Image = await CreateBitmapAsync(imageBytes);
                game.ImageFileName = imageFileName;
                game.HasBackup = backupExists;

                if (updateStatusText)
                {
                    StatusText.Text = (game.Name == unknownName ? $"Artwork {imageFileName} updated successfully" : $"Artwork for {game.Name} updated successfully") + $" - {restartHint}";
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in DownloadAndReplaceImageAsync for {game.Name}: {ex.Message}");

                return false;
            }
        }

        /// <summary>
        /// Writes the image the Xbox app displays, clearing and re-applying the read-only protection around the write.
        /// </summary>
        private static async Task WriteArtworkFileAsync(StorageFolder folder, string imageFileName, IBuffer imageBytes)
        {
            string imagePath = Path.Combine(folder.Path, imageFileName);

            ArtworkFileLock.Unlock(imagePath);

            StorageFile imageFile = await folder.CreateFileAsync(imageFileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBufferAsync(imageFile, imageBytes);

            if (AppSettings.LockArtwork)
            {
                ArtworkFileLock.Lock(imagePath);
            }
        }

        /// <summary>
        /// Handle edit button click to show grid selection panel
        /// </summary>
        private async void EditGameImage_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;

            if (button?.Tag is GameEntry gameEntry)
            {
                lastFocusedButton = button;
                CurrentSelectedGame = gameEntry;

                await LoadGridSelectionPanelAsync(gameEntry);
            }
        }

        /// <summary>
        /// Load and display available grids for the selected game
        /// </summary>
        private async Task LoadGridSelectionPanelAsync(GameEntry game)
        {
            int requestId = ++gridPanelRequestId;

            try
            {
                // Update panel header with game info
                GridPanelHeaderText = $"Select artwork for {game.Name} (platform: {game.Platform}, ID: {game.XboxPlatformId})";

                // Show panel with animation
                await ShowGridPanelAsync();

                // Show loading indicator
                GridLoadingRing.IsActive = true;
                GridImagesView.Items.Clear();
                GridPanelStatus.Text = $"Loading artworks for {game.Name ?? $"{game.Platform} / {game.XboxPlatformId}"}...";

                List<SteamGridDbGrid> grids = new List<SteamGridDbGrid>();
                List<SteamGridDbGrid> icons = new List<SteamGridDbGrid>();

                using (SteamGridDbClient client = CreateSteamGridDbClient())
                {
                    if (client != null)
                    {
                        grids = await GetSquareGridsAsync(client, game);
                        icons = await GetSquareIconsAsync(client, game);
                    }
                }

                if (requestId != gridPanelRequestId)
                {
                    return;
                }

                AddSteamGridDbArtwork(grids, icons);

                await AddStoreArtworkAsync(game.Name, requestId, grids.Count, icons.Count);
            }
            catch (Exception ex)
            {
                GridPanelStatus.Text = $"Error: {ex.Message}";

                System.Diagnostics.Debug.WriteLine($"Error loading artworks: {ex.Message}");
            }
            finally
            {
                if (requestId == gridPanelRequestId)
                {
                    GridLoadingRing.IsActive = false;
                }
            }
        }

        /// <summary>
        /// Adds SteamGridDB grids and icons to the selection panel, highest score first.
        /// </summary>
        private void AddSteamGridDbArtwork(IList<SteamGridDbGrid> grids, IList<SteamGridDbGrid> icons)
        {
            IEnumerable<SteamGridDbGrid> sortedArtworks = (grids ?? new List<SteamGridDbGrid>())
                .Concat(icons ?? new List<SteamGridDbGrid>())
                .OrderByDescending(g => g.Score);

            foreach (SteamGridDbGrid artwork in sortedArtworks)
            {
                GridImagesView.Items.Add(new GridImageItem
                {
                    Id = artwork.Id,
                    Url = artwork.Url,
                    ThumbUrl = artwork.Thumb ?? artwork.Url,
                    Author = artwork.Author?.Name ?? unknownName,
                    Style = artwork.Style ?? "default",
                    Score = artwork.Score,
                    Source = steamGridDbSource
                });
            }

            FocusFirstArtwork();
        }

        /// <summary>
        /// Appends Xbox Store, PlayStation Store and IGN artwork for the game name, then updates the panel status.
        /// </summary>
        private async Task AddStoreArtworkAsync(string gameName, int requestId, int gridCount, int iconCount)
        {
            int storeCount = 0;
            bool searchStores = AppSettings.UseStoreSources && !string.IsNullOrEmpty(gameName) && gameName != unknownName;

            if (searchStores)
            {
                GridPanelStatus.Text = $"Found {gridCount} grid{(gridCount == 1 ? "" : "s")} and {iconCount} icon{(iconCount == 1 ? "" : "s")} on SteamGridDB, searching Xbox Store, PlayStation Store and IGN...";

                List<StoreArtwork> storeArtworks = await StoreArtworkClient.SearchAllAsync(gameName);

                if (requestId != gridPanelRequestId)
                {
                    return;
                }

                bool wasEmpty = GridImagesView.Items.Count == 0;

                foreach (StoreArtwork artwork in storeArtworks.OrderByDescending(a => a.MatchScore))
                {
                    GridImagesView.Items.Add(new GridImageItem
                    {
                        Url = artwork.Url,
                        ThumbUrl = artwork.ThumbUrl,
                        Author = artwork.Title,
                        Style = "store",
                        Source = artwork.Source
                    });

                    storeCount++;
                }

                if (wasEmpty)
                {
                    FocusFirstArtwork();
                }
            }

            if (gridCount + iconCount + storeCount == 0)
            {
                GridPanelStatus.Text = string.IsNullOrEmpty(AppSettings.SteamGridDbApiKey)
                    ? "No artworks found for this game (add a SteamGridDB API key in Settings to search SteamGridDB)"
                    : "No artworks found for this game";

                return;
            }

            GridPanelStatus.Text = $"Found {gridCount} grid{(gridCount == 1 ? "" : "s")} and {iconCount} icon{(iconCount == 1 ? "" : "s")} on SteamGridDB" +
                (searchStores ? $", {storeCount} from stores" : string.Empty);
        }

        private void FocusFirstArtwork()
        {
            // Focus the first artwork for controller navigation
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (GridImagesView.Items.Count > 0)
                {
                    // Force layout update so containers are realised
                    GridImagesView.UpdateLayout();

                    // Get the first item container and focus it
                    GridViewItem firstContainer = GridImagesView.ContainerFromIndex(0) as GridViewItem;

                    firstContainer?.Focus(FocusState.Programmatic);
                }
            });
        }

        /// <summary>
        /// Handles grid image selection. Downloads and replaces the game's image.
        /// </summary>
        private async void GridImage_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is GridImageItem gridItem && CurrentSelectedGame != null)
            {
                await DownloadAndReplaceImageAsync(gridItem);
            }
        }

        /// <summary>
        /// Downloads selected grid and replaces the game's image file.
        /// </summary>
        private async Task DownloadAndReplaceImageAsync(GridImageItem gridItem)
        {
            try
            {
                GridPanelStatus.Text = "Downloading image...";
                GridLoadingRing.IsActive = true;

                // Use the core download and replace logic
                bool success = await DownloadAndReplaceImageCoreAsync(CurrentSelectedGame, gridItem.Url);

                if (success)
                {
                    GridPanelStatus.Text = "Image updated successfully";

                    // Close panel after short delay
                    await Task.Delay(250);

                    await HideGridPanelAsync();
                }
                else
                {
                    GridPanelStatus.Text = CurrentSelectedGame?.ImageFolder == null
                        ? "The Xbox app image location for this game is not accessible"
                        : "Failed to download or save image";
                }

                GridLoadingRing.IsActive = false;
            }
            catch (Exception ex)
            {
                GridPanelStatus.Text = $"Error: {ex.Message}";
                GridLoadingRing.IsActive = false;
                System.Diagnostics.Debug.WriteLine($"Error downloading image: {ex.Message}");
            }
        }

        /// <summary>
        /// Show the grid selection panel with animation
        /// </summary>
        private async Task ShowGridPanelAsync()
        {
            GridSelectionPanel.Visibility = Visibility.Visible;

            // Slide up from bottom animation (like Xbox notifications)
            DoubleAnimation animation = new DoubleAnimation
            {
                From = 800,  // Start below screen
                To = 0,      // End at normal position
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            Storyboard storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, GridPanelTransform);
            Storyboard.SetTargetProperty(animation, "Y");  // Animate Y instead of X

            storyboard.Begin();

            await Task.Delay(250);
        }

        /// <summary>
        /// Hide the grid selection panel with animation.
        /// </summary>
        private async Task HideGridPanelAsync()
        {
            // Discard any artwork still being loaded for this panel
            gridPanelRequestId++;

            // Slide down animation (reverse)
            DoubleAnimation animation = new DoubleAnimation
            {
                From = 0,
                To = 800,  // Slide below screen
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            Storyboard storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, GridPanelTransform);
            Storyboard.SetTargetProperty(animation, "Y");  // Animate Y instead of X

            storyboard.Begin();

            await Task.Delay(200);

            GridSelectionPanel.Visibility = Visibility.Collapsed;
            GridImagesView.Items.Clear();
            GridLoadingRing.IsActive = false;
            CurrentSelectedGame = null;

            // Restore focus to the button that opened this panel
            lastFocusedButton?.Focus(FocusState.Programmatic);
            lastFocusedButton = null;

            // Apply a library change that arrived while the panel was open
            ReloadIfPending();
        }

        /// <summary>
        /// Handles close button click.
        /// </summary>
        private async void CloseGridPanel_Click(object sender, RoutedEventArgs e)
        {
            await HideGridPanelAsync();
        }

        /// <summary>
        /// Handles search button click to show game search panel.
        /// </summary>
        private async void SearchGameImage_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;

            if (button?.Tag is GameEntry gameEntry)
            {
                lastFocusedButton = button;
                CurrentSelectedGame = gameEntry;

                await ShowSearchPanelAsync();
            }
        }

        /// <summary>
        /// Handles search box key down (Enter to search).
        /// </summary>
        private async void GameSearchBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                await PerformGameSearchAsync();
            }
        }

        /// <summary>
        /// Handles search button click.
        /// </summary>
        private async void SearchGames_Click(object sender, RoutedEventArgs e)
        {
            await PerformGameSearchAsync();
        }

        /// <summary>
        /// Performs game search using SteamGridDB API, plus a store-only option.
        /// </summary>
        private async Task PerformGameSearchAsync()
        {
            try
            {
                string searchTerm = GameSearchBox.Text?.Trim();

                if (string.IsNullOrEmpty(searchTerm))
                {
                    SearchPanelStatus.Text = "Please enter a game name";

                    return;
                }

                SearchLoadingRing.IsActive = true;
                SearchResultsListView.Items.Clear();
                SearchPanelStatus.Text = $"Searching for '{searchTerm}'...";

                List<SteamGridDbGame> results = new List<SteamGridDbGame>();

                using (SteamGridDbClient client = CreateSteamGridDbClient())
                {
                    if (client != null)
                    {
                        results = await client.SearchGameByNameAsync(searchTerm);
                    }
                }

                // Add results to list
                foreach (SteamGridDbGame game in results)
                {
                    SearchResultsListView.Items.Add(game);
                }

                if (AppSettings.UseStoreSources)
                {
                    SearchResultsListView.Items.Add(new SteamGridDbGame
                    {
                        Id = 0,
                        Name = searchTerm,
                        Subtitle = "Search Xbox Store, PlayStation Store and IGN artwork for this name"
                    });
                }

                if (string.IsNullOrEmpty(AppSettings.SteamGridDbApiKey))
                {
                    SearchPanelStatus.Text = "SteamGridDB search needs an API key (see Settings)";
                }
                else
                {
                    SearchPanelStatus.Text = results.Count == 0 ? "No games found on SteamGridDB" : $"Found {results.Count} game{(results.Count == 1 ? "" : "s")}";
                }

                SearchLoadingRing.IsActive = false;
            }
            catch (Exception ex)
            {
                SearchPanelStatus.Text = $"Error: {ex.Message}";
                SearchLoadingRing.IsActive = false;
                System.Diagnostics.Debug.WriteLine($"Error searching games: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle search result selection
        /// </summary>
        private async void SearchResult_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SteamGridDbGame selectedGame)
            {
                // DO NOT update current game's name - keep it as "Unknown" so the user can search again

                // Hide search panel but don't clear lastFocusedButton yet
                await HideSearchPanelAsync(false);
                await LoadGridSelectionByGameIdAsync(selectedGame);
            }
        }

        /// <summary>
        /// Loads grid selection panel for a game by its SteamGridDB ID (or store artwork only for the synthetic result).
        /// </summary>
        private async Task LoadGridSelectionByGameIdAsync(SteamGridDbGame game)
        {
            int requestId = ++gridPanelRequestId;

            try
            {
                // Update panel header
                GridPanelHeaderText = game.IsStoreSearchOnly
                    ? $"Select store artwork for {game.Name}"
                    : $"Select artwork for {game.Name} (SteamGridDB ID: {game.Id})";

                // Show panel with animation
                await ShowGridPanelAsync();

                // Show loading indicator
                GridLoadingRing.IsActive = true;
                GridImagesView.Items.Clear();
                GridPanelStatus.Text = $"Loading artworks for {game.Name}...";

                List<SteamGridDbGrid> grids = new List<SteamGridDbGrid>();
                List<SteamGridDbGrid> icons = new List<SteamGridDbGrid>();

                if (!game.IsStoreSearchOnly)
                {
                    using (SteamGridDbClient client = CreateSteamGridDbClient())
                    {
                        if (client != null)
                        {
                            grids = await client.GetSquareGridsByGameIdAsync(game.Id);
                            icons = await client.GetSquareIconsByGameIdAsync(game.Id);
                        }
                    }
                }

                if (requestId != gridPanelRequestId)
                {
                    return;
                }

                AddSteamGridDbArtwork(grids, icons);

                await AddStoreArtworkAsync(game.Name, requestId, grids.Count, icons.Count);
            }
            catch (Exception ex)
            {
                GridPanelStatus.Text = $"Error: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Error loading artworks: {ex.Message}");
            }
            finally
            {
                if (requestId == gridPanelRequestId)
                {
                    GridLoadingRing.IsActive = false;
                }
            }
        }

        /// <summary>
        /// Shows the search panel with animation.
        /// </summary>
        private async Task ShowSearchPanelAsync()
        {
            // Update header with game information
            if (CurrentSelectedGame != null)
            {
                if (CurrentSelectedGame.Name == unknownName)
                {
                    SearchPanelHeaderText = $"Manual search for a game from {CurrentSelectedGame.Platform}, ID: {CurrentSelectedGame.ExternalPlatformId}";
                }
                else
                {
                    SearchPanelHeaderText = $"Manual search for {CurrentSelectedGame.Name}";
                }
            }
            else
            {
                SearchPanelHeaderText = "Manual search";
            }

            GameSearchPanel.Visibility = Visibility.Visible;

            // Prefill search box with game name if it's not "Unknown"
            if (CurrentSelectedGame != null && CurrentSelectedGame.Name != unknownName)
            {
                GameSearchBox.Text = CurrentSelectedGame.Name;
            }
            else
            {
                GameSearchBox.Text = string.Empty;
            }

            SearchResultsListView.Items.Clear();
            SearchPanelStatus.Text = "Enter game name to search";

            // Slide up from bottom animation
            DoubleAnimation animation = new DoubleAnimation
            {
                From = 800,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            Storyboard storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, SearchPanelTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            storyboard.Begin();

            await Task.Delay(250);

            // Focus search box if empty, otherwise focus search button
            if (!string.IsNullOrEmpty(GameSearchBox.Text))
            {
                SearchGamesButton.Focus(FocusState.Programmatic);
            }
            else
            {
                GameSearchBox.Focus(FocusState.Programmatic);

                // Position cursor at the end of the text
                GameSearchBox.Select(GameSearchBox.Text.Length, 0);
            }
        }

        /// <summary>
        /// Hide the search panel with animation
        /// </summary>
        private async Task HideSearchPanelAsync(bool restoreFocus = true)
        {
            // Slide down animation
            DoubleAnimation animation = new DoubleAnimation
            {
                From = 0,
                To = 800,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            Storyboard storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, SearchPanelTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            storyboard.Begin();

            await Task.Delay(200);

            GameSearchPanel.Visibility = Visibility.Collapsed;
            SearchResultsListView.Items.Clear();

            // Restore focus to the button that opened this panel
            if (restoreFocus && lastFocusedButton != null)
            {
                lastFocusedButton.Focus(FocusState.Programmatic);
                lastFocusedButton = null;
            }

            // Not when switching straight to the artwork panel
            if (restoreFocus)
            {
                ReloadIfPending();
            }
        }

        /// <summary>
        /// Handle close search panel button click
        /// </summary>
        private async void CloseSearchPanel_Click(object sender, RoutedEventArgs e)
        {
            await HideSearchPanelAsync();
        }

        /// <summary>
        /// Handle restore backup button click
        /// </summary>
        private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;

            if (button?.Tag is GameEntry gameEntry)
            {
                await RestoreBackupAsync(gameEntry);
            }
        }

        /// <summary>
        /// Restore image from backup file
        /// </summary>
        private async Task RestoreBackupAsync(GameEntry game)
        {
            string imageFileName = Path.GetFileName(game.ImageFilePath ?? string.Empty);
            string backupGameName = game.Name != unknownName ? game.Name : imageFileName;

            try
            {
                if (game.ImageFolder == null || string.IsNullOrEmpty(imageFileName))
                {
                    StatusText.Text = $"The image location for {backupGameName} is not accessible";

                    return;
                }

                StatusText.Text = $"Restoring backup for {backupGameName}...";

                string backupFileName = GetSiblingFileName(imageFileName, backupImageExtension);
                string newFileName = GetSiblingFileName(imageFileName, newImageExtension);

                if (!(await game.ImageFolder.TryGetItemAsync(backupFileName) is StorageFile backupFile))
                {
                    StatusText.Text = $"Backup file not found for {backupGameName}";

                    return;
                }

                // The original artwork goes back under the Xbox app's control, so remove the protection
                ArtworkFileLock.Unlock(Path.Combine(game.ImageFolder.Path, imageFileName));

                // Delete the current and saved custom images if they exist
                if (await game.ImageFolder.TryGetItemAsync(imageFileName) is StorageFile currentImageFile)
                {
                    await currentImageFile.DeleteAsync();
                }

                if (await game.ImageFolder.TryGetItemAsync(newFileName) is StorageFile newImageFile)
                {
                    await newImageFile.DeleteAsync();
                }

                // Rename backup to become the main image
                await backupFile.RenameAsync(imageFileName, NameCollisionOption.ReplaceExisting);

                // Reload the image in the UI
                StorageFile imageFile = await game.ImageFolder.GetFileAsync(imageFileName);

                game.Image = await LoadBitmapAsync(imageFile);
                game.ImageFileName = imageFileName;
                game.HasBackup = false; // Backup no longer exists

                StatusText.Text = $"Backup restored for {backupGameName}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error restoring backup for {backupGameName}: {ex.Message}";

                System.Diagnostics.Debug.WriteLine($"Error in RestoreBackupAsync for {backupGameName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads an image file into memory so the file is not kept open (an open stream stops the Xbox app from updating it).
        /// </summary>
        private static async Task<BitmapImage> LoadBitmapAsync(StorageFile file)
        {
            IBuffer buffer = await FileIO.ReadBufferAsync(file);

            return await CreateBitmapAsync(buffer);
        }

        private static async Task<BitmapImage> CreateBitmapAsync(IBuffer buffer)
        {
            // The stream is in memory only and must stay alive for the BitmapImage, so it is not disposed here
            InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(buffer);
            stream.Seek(0);

            // Covers are shown at 80px; decoding a 1024px image at full size costs ~4 MB of memory per game
            BitmapImage image = new BitmapImage { DecodePixelWidth = thumbnailDecodeSize, DecodePixelType = DecodePixelType.Logical };
            await image.SetSourceAsync(stream);

            return image;
        }

        /// <summary>
        /// Returns the file name with its extension replaced, e.g. steam_123.png -> steam_123.bak.
        /// </summary>
        private static string GetSiblingFileName(string fileName, string newExtension)
        {
            return Path.GetFileNameWithoutExtension(fileName) + newExtension;
        }

        /// <summary>
        /// Reads a JSON property as a string, accepting numbers too. Returns null when missing.
        /// </summary>
        private static string GetJsonString(JsonObject obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, out IJsonValue value))
            {
                return null;
            }

            switch (value.ValueType)
            {
                case JsonValueType.String:
                    return value.GetString();
                case JsonValueType.Number:
                    return ((long)value.GetNumber()).ToString();
                default:
                    return null;
            }
        }

        /// <summary>
        /// Fetches game name from the Steam store API by app ID.
        /// </summary>
        /// <param name="appId">The Steam app ID</param>
        /// <returns>Game name or null if not found</returns>
        private async Task<string> GetSteamGameNameAsync(string appId)
        {
            try
            {
                string url = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&filters=basic";
                HttpResponseMessage response = await sharedHttpClient.GetAsync(new Uri(url));

                if (response.IsSuccessStatusCode &&
                    JsonObject.TryParse(await response.Content.ReadAsStringAsync(), out JsonObject root) &&
                    root.TryGetValue(appId, out IJsonValue appValue) && appValue.ValueType == JsonValueType.Object)
                {
                    JsonObject app = appValue.GetObject();

                    if (app.GetNamedBoolean("success", false) && app.TryGetValue("data", out IJsonValue data) && data.ValueType == JsonValueType.Object)
                    {
                        string name = GetJsonString(data.GetObject(), "name");

                        // Steam names often carry trademark symbols that would break exact matching elsewhere
                        return name?.Replace("™", string.Empty).Replace("®", string.Empty).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching Steam game name for {appId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Fetches game name from GOG API by GOG ID.
        /// </summary>
        /// <param name="gogId">The GOG game ID</param>
        /// <returns>Game name or null if not found</returns>
        private async Task<string> GetGogGameNameAsync(string gogId)
        {
            try
            {
                string url = $"https://api.gog.com/v2/games/{gogId}";
                HttpResponseMessage response = await sharedHttpClient.GetAsync(new Uri(url));

                if (response.IsSuccessStatusCode)
                {
                    string jsonContent = await response.Content.ReadAsStringAsync();

                    if (JsonObject.TryParse(jsonContent, out JsonObject gameData))
                    {
                        if (gameData.ContainsKey("_embedded") &&
                            gameData.GetNamedObject("_embedded").ContainsKey("product"))
                        {
                            JsonObject product = gameData.GetNamedObject("_embedded").GetNamedObject("product");

                            if (product.ContainsKey("title"))
                            {
                                return product.GetNamedString("title");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching GOG game name for {gogId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Fetches Epic Games Store game name from GitHub by external platform ID.
        /// </summary>
        /// <param name="epicId">The Epic Games Store ID.</param>
        /// <returns>Game name or null if not found.</returns>
        private async Task<string> GetEpicGameNameAsync(string epicId)
        {
            try
            {
                string url = $"https://raw.githubusercontent.com/nachoaldamav/items-tracker/refs/heads/main/database/items/{epicId}.json";
                HttpResponseMessage response = await sharedHttpClient.GetAsync(new Uri(url));

                if (response.IsSuccessStatusCode)
                {
                    string jsonContent = await response.Content.ReadAsStringAsync();

                    if (JsonObject.TryParse(jsonContent, out JsonObject gameData))
                    {
                        if (gameData.ContainsKey("title"))
                        {
                            return gameData.GetNamedString("title");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching Epic game name for {epicId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Downloads and parses the Ubisoft game list from GitHub.
        /// </summary>
        /// <returns>True if successful, false otherwise</returns>
        private async Task<bool> LoadUbisoftGameListAsync()
        {
            if (ubisoftGameLookupCache != null)
            {
                return true;
            }

            try
            {
                string url = "https://raw.githubusercontent.com/Haoose/UPLAY_GAME_ID/refs/heads/master/README.md";
                HttpResponseMessage response = await sharedHttpClient.GetAsync(new Uri(url));

                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                string content = await response.Content.ReadAsStringAsync();
                string[] lines = content.Split('\n');

                Dictionary<string, string> lookup = new Dictionary<string, string>();

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();

                    if (string.IsNullOrEmpty(trimmedLine))
                    {
                        continue;
                    }

                    // Format: "232 - Beyond Good and Evil™"
                    int dashIndex = trimmedLine.IndexOf(" - ");

                    if (dashIndex > 0)
                    {
                        string idPart = trimmedLine.Substring(0, dashIndex).Trim();
                        string namePart = trimmedLine.Substring(dashIndex + 3).Trim();

                        if (!string.IsNullOrEmpty(idPart) && !string.IsNullOrEmpty(namePart))
                        {
                            lookup[idPart] = namePart;
                        }
                    }
                }

                // Only publish the cache once complete - lookups for several Ubisoft games now run concurrently
                ubisoftGameLookupCache = lookup;

                return ubisoftGameLookupCache.Count > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading Ubisoft game list: {ex.Message}");

                return false;
            }
        }

        /// <summary>
        /// Fetches game name from cached Ubisoft game list by Ubisoft ID.
        /// </summary>
        /// <param name="ubisoftId">The Ubisoft game ID</param>
        /// <returns>Game name or null if not found</returns>
        private async Task<string> GetUbisoftGameNameAsync(string ubisoftId)
        {
            try
            {
                await LoadUbisoftGameListAsync();

                if (ubisoftGameLookupCache != null && ubisoftGameLookupCache.TryGetValue(ubisoftId, out string gameName))
                {
                    return gameName;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching Ubisoft game name for {ubisoftId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Handles the GotFocus event for the game search box, positioning the cursor at the end of the text and
        /// displaying the virtual keyboard when appropriate.
        /// </summary>
        /// <remarks>The virtual keyboard is shown only when focus is received via keyboard or gamepad
        /// navigation, not when using mouse or touch input. This behavior ensures that the keyboard does not appear
        /// unintentionally when the user clicks or taps the search box.</remarks>
        /// <param name="sender">The source of the event, expected to be a TextBox representing the game search box.</param>
        /// <param name="e">The event data associated with the GotFocus event.</param>
        private async void GameSearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            // Position cursor at the end of the text
            if (sender is TextBox textBox)
            {
                textBox.SelectionStart = textBox.Text.Length;
                textBox.SelectionLength = 0;

                // Only show virtual keyboard for gamepad/controller input
                // FocusState.Keyboard indicates focus via keyboard/gamepad navigation
                // FocusState.Pointer indicates mouse/touch click - don't show keyboard for this
                if (textBox.FocusState == FocusState.Keyboard)
                {
                    // Delay showing the keyboard to prevent Game Bar from hiding on first focus
                    await Task.Delay(100);

                    try
                    {
                        CoreInputView.GetForCurrentView().TryShow((CoreInputViewKind)7); // 7 = keyboard gamepad
                    }
                    catch
                    {
                        // Keyboard input view not available or failed to show
                    }
                }
            }
        }

        /// <summary>
        /// Handles the LostFocus event for the game search box to hide the virtual keyboard when the control loses
        /// focus.
        /// </summary>
        /// <param name="sender">The source of the event, typically the game search box control.</param>
        /// <param name="e">The event data associated with the LostFocus event.</param>
        private void GameSearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // Hide virtual keyboard when focus is lost
            try
            {
                CoreInputView.GetForCurrentView().TryHide();
            }
            catch
            {
                // Keyboard input view not available or failed to hide
            }
        }
    }
}
