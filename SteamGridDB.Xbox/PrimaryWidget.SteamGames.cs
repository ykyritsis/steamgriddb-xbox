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
    // Adding installed Steam games that the Xbox app does not show, as custom library entries.
    public sealed partial class PrimaryWidget
    {
        /// <summary>
        /// Finds Steam games that are installed but missing from the Xbox app and adds them as custom library entries
        /// that launch through Steam (steam://rungameid/...), with cover art.
        /// </summary>
        private async void AddMissingSteamGamesButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy)
            {
                StatusText.Text = "Please wait for the current operation to finish";

                return;
            }

            IsBusy = true;

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
                           $"{coverHint}\n\nThe Xbox app restarts briefly while its library is updated.",
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

                // The companion restarts the Xbox app around the change itself; without it, close the app first
                if (!CustomLibraryWriter.IsHelperAvailable && !await EnsureXboxAppClosedAsync())
                {
                    StatusText.Text = "No games were added";

                    return;
                }

                StatusText.Text = "Updating the Xbox app's library...";

                List<string> addedIds = await CustomLibraryWriter.AddEntriesAsync(entries);

                bool reopened = CustomLibraryWriter.LastCommitUsedHelper
                    ? CustomLibraryWriter.LastCommitReopenedXboxApp
                    : await ReopenXboxAppAsync();

                // Watch for a while: an overwrite shows up within seconds of the Xbox app starting
                int kept = 0;

                for (int check = 0; check < 4; check++)
                {
                    await Task.Delay(2000);

                    kept = await CustomLibraryWriter.CountEntriesAsync(addedIds);

                    if (kept < addedIds.Count)
                    {
                        break;
                    }
                }

                string nextStep = reopened ? "the Xbox app has been reopened" : $"open the Xbox app to see {(kept == 1 ? "it" : "them")}";

                StatusText.Text = kept == addedIds.Count
                    ? $"Added {kept} Steam game{(kept == 1 ? string.Empty : "s")} to the Xbox app - {nextStep}"
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
                IsBusy = false;

                ReloadIfPending();
            }
        }

        /// <summary>
        /// Closes the Xbox app completely before its custom library is written, asking the user to close it when that
        /// cannot be done automatically.
        /// </summary>
        /// <returns>False if the user cancelled.</returns>
        private async Task<bool> EnsureXboxAppClosedAsync()
        {
            StatusText.Text = "Updating the Xbox app's library...";

            // Remember whether to bring the Xbox app back afterwards
            reopenXboxAppAfterWrite = await CustomLibraryWriter.GetRunningProcessCountAsync() > 0;

            bool closedAutomatically = await CustomLibraryWriter.TryCloseXboxAppAsync();

            // Any Xbox app process left running saves its own copy of the library over the file (seen in testing:
            // entries written while XboxPcTray was still running were wiped), so do not write until it has exited
            int stillRunning = closedAutomatically ? 0 : await CustomLibraryWriter.GetRunningProcessCountAsync();

            while (stillRunning != 0)
            {
                ContentDialog closeDialog = CreateDialog(
                    "Close the Xbox app",
                    (stillRunning > 0 ? $"The Xbox app is still running ({stillRunning} process{(stillRunning == 1 ? string.Empty : "es")}). " : "The Xbox app could not be closed automatically. ") +
                    "It saves over its library while running, so please close it completely - including its icon in the system tray " +
                    "(right-click > Exit) - then press Continue.",
                    "Continue");

                if (await closeDialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return false;
                }

                // -1 means it cannot be checked without diagnostic access - trust the user
                stillRunning = await CustomLibraryWriter.GetRunningProcessCountAsync();

                if (stillRunning < 0)
                {
                    break;
                }
            }

            await Task.Delay(1000);

            return true;
        }

        // Set by EnsureXboxAppClosedAsync: the Xbox app was open, so it is reopened once the library has been written
        private bool reopenXboxAppAfterWrite;

        /// <summary>
        /// Brings the Xbox app back after its library was written, if it was open before. Returns true when it is running.
        /// </summary>
        private async Task<bool> ReopenXboxAppAsync()
        {
            await Task.Delay(1500);

            if (await CustomLibraryWriter.GetRunningProcessCountAsync() > 0)
            {
                // In Xbox full screen experience Windows restarts the Xbox app within a second of closing it, so it may
                // already have loaded the old library - close it once more so the next start reads the new one
                await CustomLibraryWriter.TryCloseXboxAppAsync();
                await Task.Delay(1500);

                if (await CustomLibraryWriter.GetRunningProcessCountAsync() > 0)
                {
                    return true;
                }

                reopenXboxAppAfterWrite = true;
            }

            return reopenXboxAppAfterWrite && await CustomLibraryWriter.TryLaunchXboxAppAsync();
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
    }
}
