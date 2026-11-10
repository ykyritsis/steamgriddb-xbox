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
    // The artwork selection panel.
    public sealed partial class PrimaryWidget
    {
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
                        if (!game.SteamGridDbGameId.HasValue && GamePlatformHelper.IsCustomLibrary(game.Platform) && game.Name != unknownName)
                        {
                            // Custom games have no platform ID: use the closest SteamGridDB result for their name
                            SteamGridDbGame closest = (await client.SearchGameByNameAsync(game.Name)).FirstOrDefault();

                            if (closest != null)
                            {
                                GridPanelHeaderText = $"Select artwork for {game.Name} (closest SteamGridDB match: {closest.Name})";
                                grids = await client.GetSquareGridsByGameIdAsync(closest.Id);
                                icons = await client.GetSquareIconsByGameIdAsync(closest.Id);
                            }
                        }
                        else
                        {
                            grids = await GetSquareGridsAsync(client, game);
                            icons = await GetSquareIconsAsync(client, game);
                        }
                    }
                }

                if (requestId != gridPanelRequestId)
                {
                    return;
                }

                AddSteamGridDbArtwork(grids, icons);

                int appIconCount = 0;

                if (GamePlatformHelper.IsCustomLibrary(game.Platform))
                {
                    // Covers built from the program's own icon - always available, even when no site knows the app
                    List<AppIconTile> tiles = await AppIconTiles.CreateTilesAsync(game.XboxPlatformId, game.ExternalPlatformId);

                    if (requestId != gridPanelRequestId)
                    {
                        return;
                    }

                    for (int i = 0; i < tiles.Count; i++)
                    {
                        GridImageItem item = new GridImageItem
                        {
                            Url = tiles[i].Uri,
                            ThumbUrl = tiles[i].Uri,
                            Author = $"{tiles[i].Style} background",
                            Style = tiles[i].Style,
                            Source = AppIconTiles.SourceName
                        };

                        // For apps the icon tile is usually the best choice, so show it first
                        if (game.Platform == GamePlatform.App)
                        {
                            GridImagesView.Items.Insert(i, item);
                        }
                        else
                        {
                            GridImagesView.Items.Add(item);
                        }
                    }

                    appIconCount = tiles.Count;

                    if (tiles.Count > 0)
                    {
                        FocusFirstArtwork();
                    }
                }

                // Game stores only return unrelated games for app names
                await AddStoreArtworkAsync(game.Platform == GamePlatform.App ? null : game.Name, requestId, grids.Count, icons.Count, appIconCount);
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
        private async Task AddStoreArtworkAsync(string gameName, int requestId, int gridCount, int iconCount, int appIconCount = 0)
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

            string appIconText = appIconCount > 0 ? $"{appIconCount} made from the app's icon, " : string.Empty;

            if (gridCount + iconCount + storeCount + appIconCount == 0)
            {
                GridPanelStatus.Text = string.IsNullOrEmpty(AppSettings.SteamGridDbApiKey)
                    ? "No artworks found for this game (add a SteamGridDB API key in Settings to search SteamGridDB)"
                    : "No artworks found for this game";

                return;
            }

            string summary = $"{appIconText}found {gridCount} grid{(gridCount == 1 ? "" : "s")} and {iconCount} icon{(iconCount == 1 ? "" : "s")} on SteamGridDB" +
                (searchStores ? $", {storeCount} from stores" : string.Empty);

            GridPanelStatus.Text = char.ToUpper(summary[0]) + summary.Substring(1);
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
            GameEntry game = CurrentSelectedGame;

            await ApplyPanelArtworkAsync("Downloading image...", () => DownloadAndReplaceImageCoreAsync(game, gridItem.Url));
        }

        /// <summary>
        /// Opens the search panel for the game in the artwork panel, e.g. when the closest match is the wrong game.
        /// </summary>
        private async void SearchOtherName_Click(object sender, RoutedEventArgs e)
        {
            GameEntry game = CurrentSelectedGame;
            Button opener = lastFocusedButton;

            if (game == null)
            {
                return;
            }

            // Keep the game selected and the original button to return focus to after the search
            lastFocusedButton = null;
            await HideGridPanelAsync();

            lastFocusedButton = opener;
            CurrentSelectedGame = game;

            await ShowSearchPanelAsync();
        }

        /// <summary>
        /// Uses artwork from any image link the user pastes.
        /// </summary>
        private async void UseImageLink_Click(object sender, RoutedEventArgs e)
        {
            GameEntry game = CurrentSelectedGame;

            if (game == null)
            {
                return;
            }

            TextBox linkBox = new TextBox
            {
                Header = "Link to an image (PNG, JPEG, WebP...) - it will be cropped to a square",
                PlaceholderText = "https://"
            };
            linkBox.GotFocus += GameSearchBox_GotFocus;
            linkBox.LostFocus += GameSearchBox_LostFocus;

            if (await CreateDialog("Use an image link", linkBox, "Use image").ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!Uri.TryCreate(linkBox.Text?.Trim(), UriKind.Absolute, out Uri link) || (link.Scheme != "https" && link.Scheme != "http"))
            {
                GridPanelStatus.Text = "That is not a valid web link";

                return;
            }

            await ApplyPanelArtworkAsync("Downloading image...", () => DownloadAndReplaceImageCoreAsync(game, link.AbsoluteUri));
        }

        /// <summary>
        /// Uses an image file from this PC.
        /// </summary>
        private async void UseImageFile_Click(object sender, RoutedEventArgs e)
        {
            GameEntry game = CurrentSelectedGame;

            if (game == null)
            {
                return;
            }

            Windows.Storage.Pickers.FileOpenPicker picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary
            };

            foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico" })
            {
                picker.FileTypeFilter.Add(extension);
            }

            StorageFile file;

            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                // Pickers are not available in every Game Bar context
                GridPanelStatus.Text = "The file picker is not available here - use an image link instead";

                System.Diagnostics.Debug.WriteLine($"File picker failed: {ex.Message}");

                return;
            }

            if (file == null)
            {
                return;
            }

            await ApplyPanelArtworkAsync("Applying image...", async () => await ReplaceImageCoreAsync(game, await FileIO.ReadBufferAsync(file)));
        }

        /// <summary>
        /// Runs an artwork change from the artwork panel, showing progress and closing the panel when it succeeds.
        /// </summary>
        private async Task ApplyPanelArtworkAsync(string progressText, Func<Task<bool>> applyArtwork)
        {
            try
            {
                GridPanelStatus.Text = progressText;
                GridLoadingRing.IsActive = true;

                bool success = await applyArtwork();

                if (success)
                {
                    GridPanelStatus.Text = "Image updated successfully";

                    // Close panel after short delay
                    await Task.Delay(250);

                    await HideGridPanelAsync();
                }
                else
                {
                    GridPanelStatus.Text = CurrentSelectedGame != null && !CanReplaceImage(CurrentSelectedGame)
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
    }
}
