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
    // The settings dialog.
    public sealed partial class PrimaryWidget
    {
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
    }
}
