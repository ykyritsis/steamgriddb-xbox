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
    // Applying, restoring and bulk-fixing artwork.
    public sealed partial class PrimaryWidget
    {
        /// <summary>
        /// Handles fix library button click to automatically download artwork for all eligible games.
        /// </summary>
        private async void FixLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy)
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
            if (IsBusy)
            {
                StatusText.Text = "Please wait for the current operation to finish";

                return;
            }

            ContentDialog confirmDialog = CreateDialog(
                "Re-apply my covers",
                "This puts back every cover you chose with this widget (useful if the Xbox app reset them).\n\n" +
                "Do you want to continue?",
                "Re-apply my covers");

            ContentDialogResult result = await confirmDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                await RestoreAllChangesAsync();
            }
        }

        /// <summary>
        /// Automatically downloads the best artwork for games with a match and no backup.
        /// </summary>
        private async Task FixLibraryAsync()
        {
            IsBusy = true;

            try
            {
                bool useStores = AppSettings.UseStoreSources;

                // Get eligible games: not modified yet, writable, and something to match on
                List<GameEntry> eligibleGames = GameEntries
                    .Where(g => !g.HasBackup && g.ImageFolder != null && g.Platform != GamePlatform.App && (g.HasSteamGridDBMatch || (useStores && g.Name != unknownName)))
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
                IsBusy = false;

                // Shows the empty-tab hint now that loading has finished
                RefreshDisplayedEntries();

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
            IsBusy = true;

            try
            {
                StatusText.Text = "Re-applying your covers...";

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
                        string extension = Path.GetExtension(imageFileName).ToLowerInvariant();

                        // Re-encode through the converter so covers saved by older versions (some only 128 px, which the
                        // Xbox app ignores) are brought up to the minimum size
                        imageBytes = await ArtworkImageConverter.ToSquareImageAsync(imageBytes, extension == ".jpg" || extension == ".jpeg");

                        await WriteArtworkFileAsync(game.ImageFolder, imageFileName, imageBytes);
                        await FileIO.WriteBufferAsync(newFile, imageBytes);

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
                    StatusText.Text = "No covers of yours to re-apply";
                }
                else
                {
                    StatusText.Text = $"Done: {successCount} re-applied, {noArtworkCount} had no cover of yours saved, {errorCount} error{(errorCount == 1 ? string.Empty : "s")}" +
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
                IsBusy = false;

                // Shows the empty-tab hint now that loading has finished
                RefreshDisplayedEntries();

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
            if (game == null || !CanReplaceImage(game))
            {
                return false;
            }

            try
            {
                // Tiles made from an app's icon are files in the widget's own storage, not web downloads
                if (imageUrl.StartsWith(AppIconTiles.AppDataPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return await ReplaceImageCoreAsync(game, await AppIconTiles.ReadTileAsync(imageUrl), updateStatusText);
                }

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

                return await ReplaceImageCoreAsync(game, downloadedBytes, updateStatusText);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error downloading artwork for {game.Name}: {ex.Message}");

                return false;
            }
        }

        /// <summary>
        /// Whether the widget can write artwork for this game. Custom games without an image get one created for them.
        /// </summary>
        private static bool CanReplaceImage(GameEntry game)
        {
            return GamePlatformHelper.IsCustomLibrary(game.Platform) || (game.ImageFolder != null && !string.IsNullOrEmpty(game.ImageFilePath));
        }

        /// <summary>
        /// Replaces a game's artwork with the given image (any common format; it is converted to a square image first).
        /// </summary>
        private async Task<bool> ReplaceImageCoreAsync(GameEntry game, IBuffer sourceImage, bool updateStatusText = true)
        {
            try
            {
                // A custom game with no usable image file gets a new one in the Xbox app's custom images folder
                if (GamePlatformHelper.IsCustomLibrary(game.Platform) && (game.ImageFolder == null || string.IsNullOrEmpty(game.ImageFilePath)))
                {
                    return await CreateCustomGameImageAsync(game, sourceImage, updateStatusText);
                }

                if (game.ImageFolder == null || string.IsNullOrEmpty(game.ImageFilePath))
                {
                    return false;
                }

                IBuffer downloadedBytes = sourceImage;

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
                System.Diagnostics.Debug.WriteLine($"Error in ReplaceImageCoreAsync for {game.Name}: {ex.Message}");

                return false;
            }
        }

        /// <summary>
        /// Gives a custom game that has no image its first cover: saves it to the Xbox app's custom images folder and
        /// records the path in the custom library (the Xbox app must be closed for that, as when adding games).
        /// </summary>
        private async Task<bool> CreateCustomGameImageAsync(GameEntry game, IBuffer sourceImage, bool updateStatusText)
        {
            StorageFolder imagesFolder = await CustomLibraryWriter.GetImagesFolderAsync();
            string safeId = new string(game.XboxPlatformId.Where(char.IsLetterOrDigit).ToArray());
            string imageFileName = $"sgdbx-custom-{safeId}.png";
            IBuffer imageBytes = await ArtworkImageConverter.ToSquareImageAsync(sourceImage, false);

            await WriteArtworkFileAsync(imagesFolder, imageFileName, imageBytes);

            StorageFile newFile = await imagesFolder.CreateFileAsync(GetSiblingFileName(imageFileName, newImageExtension), CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBufferAsync(newFile, imageBytes);

            string imagePath = Path.Combine(imagesFolder.Path, imageFileName);

            // Only the custom library needs updating if it does not point at this file yet
            if (!string.Equals(await CustomLibraryWriter.GetImagePathAsync(game.XboxPlatformId, game.Platform == GamePlatform.App), imagePath, StringComparison.OrdinalIgnoreCase))
            {
                // The Xbox app is restarted around the change (it is reopened afterwards if it was open)
                if (!CustomLibraryWriter.IsHelperAvailable && !await EnsureXboxAppClosedAsync())
                {
                    StatusText.Text = $"The cover for {game.Name} was not added";

                    return false;
                }

                if (!await CustomLibraryWriter.SetImagePathAsync(game.XboxPlatformId, imagePath, game.Platform == GamePlatform.App))
                {
                    StatusText.Text = $"{game.Name} is no longer in the Xbox app's custom library";

                    return false;
                }

                if (!CustomLibraryWriter.LastCommitUsedHelper)
                {
                    await ReopenXboxAppAsync();
                }

                await Task.Delay(3000);

                // The Xbox app's background helper can undo edits from its memory; say so rather than claim success
                if (!string.Equals(await CustomLibraryWriter.GetImagePathAsync(game.XboxPlatformId, game.Platform == GamePlatform.App), imagePath, StringComparison.OrdinalIgnoreCase))
                {
                    StatusText.Text = $"The Xbox app undid the cover for {game.Name} - restart the PC, then try again";

                    return false;
                }

                manifestReloadPending = true;
            }

            game.ImageFolder = imagesFolder;
            game.ImageFilePath = imagePath;
            game.Image = await CreateBitmapAsync(imageBytes);
            game.ImageFileName = imageFileName;

            if (updateStatusText)
            {
                StatusText.Text = $"Cover added for {game.Name}";
            }

            return true;
        }

        /// <summary>
        /// Writes the image the Xbox app displays, clearing and re-applying the read-only protection around the write.
        /// </summary>
        private static async Task WriteArtworkFileAsync(StorageFolder folder, string imageFileName, IBuffer imageBytes)
        {
            string imagePath = Path.Combine(folder.Path, imageFileName);

            // Write to a temporary file and swap it in only once it is complete: writing in place could leave an empty,
            // locked cover behind if the write was interrupted (seen on one game), which the Xbox app then shows as blank
            StorageFile tempFile = await folder.CreateFileAsync(imageFileName + ".sgdbtmp", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBufferAsync(tempFile, imageBytes);

            if ((await tempFile.GetBasicPropertiesAsync()).Size != imageBytes.Length)
            {
                await tempFile.DeleteAsync();

                throw new IOException($"The new artwork for {imageFileName} was not written completely");
            }

            ArtworkFileLock.Unlock(imagePath);

            await tempFile.MoveAsync(folder, imageFileName, NameCollisionOption.ReplaceExisting);

            if (AppSettings.LockArtwork)
            {
                ArtworkFileLock.Lock(imagePath);
            }
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

                StatusText.Text = $"Restoring the original cover for {backupGameName}...";

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

                StatusText.Text = $"Original cover restored for {backupGameName}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error restoring backup for {backupGameName}: {ex.Message}";

                System.Diagnostics.Debug.WriteLine($"Error in RestoreBackupAsync for {backupGameName}: {ex.Message}");
            }
        }
    }
}
