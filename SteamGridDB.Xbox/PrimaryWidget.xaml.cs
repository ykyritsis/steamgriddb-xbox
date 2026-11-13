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

        /// <summary>
        /// The entries shown for the selected tab (games or apps). GameEntries stays the full list used by all operations.
        /// </summary>
        public ObservableCollection<GameEntry> DisplayedEntries
        {
            get; set;
        }

        private bool showingApps = AppSettings.ShowAppsTab;

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

        private static readonly ConcurrentDictionary<string, string> gogNameCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, string> epicNameCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HttpClient sharedHttpClient = new HttpClient();

        private Button lastFocusedButton;

        // Set while the library is loading or a bulk operation runs, so they cannot overlap
        private bool busy;

        private bool IsBusy
        {
            get => busy;
            set
            {
                busy = value;

                // Thin progress bar above the status text while anything is loading or being applied
                if (BusyBar != null)
                {
                    BusyBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        // Incremented every time the artwork panel opens or closes, so late results from a previous game are discarded
        private int gridPanelRequestId;

        // Watches the Xbox app's manifests so newly detected games appear without a manual refresh
        private StorageFileQueryResult manifestQuery;

        private StorageFileQueryResult appsManifestQuery;

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
            GameEntries = new ObservableCollection<GameEntry>();
            DisplayedEntries = new ObservableCollection<GameEntry>();
            InitializeComponent();
            Loaded += PrimaryWidget_Loaded;
        }

        private async void PrimaryWidget_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshDisplayedEntries();

            await LoadGameEntriesAsync();

            // In the background: offer the update button if a newer release exists
            var _ = CheckForUpdateAsync();

            // Set default focus to Fix my library button for controller navigation
            FixLibraryButton.Focus(FocusState.Programmatic);
        }

        private void GamesTab_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(false);
        }

        private void AppsTab_Click(object sender, RoutedEventArgs e)
        {
            SelectTab(true);
        }

        private void SelectTab(bool apps)
        {
            if (showingApps != apps)
            {
                showingApps = apps;
                AppSettings.ShowAppsTab = apps;
            }

            RefreshDisplayedEntries();
        }

        /// <summary>
        /// Fills the visible list for the selected tab and updates the tab labels and empty-tab message.
        /// </summary>
        private void RefreshDisplayedEntries()
        {
            DisplayedEntries.Clear();

            foreach (GameEntry entry in GameEntries.Where(g => (g.Platform == GamePlatform.App) == showingApps))
            {
                DisplayedEntries.Add(entry);
            }

            int appCount = GameEntries.Count(g => g.Platform == GamePlatform.App);
            int gameCount = GameEntries.Count - appCount;

            GamesTabText.Text = gameCount > 0 ? $"Games ({gameCount})" : "Games";
            AppsTabText.Text = appCount > 0 ? $"Apps ({appCount})" : "Apps";

            StyleTab(GamesTabButton, GamesTabText, !showingApps);
            StyleTab(AppsTabButton, AppsTabText, showingApps);

            // Only explain an empty tab once the library has actually loaded
            bool listShown = GameEntriesListView.Visibility == Visibility.Visible && !IsBusy;

            EmptyTabText.Text = showingApps
                ? "No custom apps yet. Add apps in the Xbox app's Apps tab with the + button, and they will show up here so you can change their icons."
                : "No games found.";
            EmptyTabText.Visibility = listShown && DisplayedEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void StyleTab(Button button, TextBlock label, bool selected)
        {
            button.Background = new Windows.UI.Xaml.Media.SolidColorBrush(selected
                ? Windows.UI.Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A)
                : Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            label.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(selected
                ? Windows.UI.Colors.White
                : Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        }

        /// <summary>
        /// Creates a SteamGridDB client, or returns null when no API key has been configured.
        /// </summary>
        private static SteamGridDbClient CreateSteamGridDbClient()
        {
            string apiKey = AppSettings.SteamGridDbApiKey;

            return string.IsNullOrEmpty(apiKey) ? null : new SteamGridDbClient(apiKey);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadGameEntriesAsync();
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
