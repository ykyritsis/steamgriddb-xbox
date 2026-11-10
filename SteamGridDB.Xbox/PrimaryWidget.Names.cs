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
    // Resolving game names and SteamGridDB matches.
    public sealed partial class PrimaryWidget
    {
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
                    CachedGameInfo cached = null;

                    try
                    {
                        cached = await GameInfoCache.TryGetAsync(cacheKey, sgdbClient != null);
                    }
                    catch (Exception ex)
                    {
                        // The cache is an optimisation only - look the game up instead
                        System.Diagnostics.Debug.WriteLine($"Game info cache lookup failed for {cacheKey}: {ex.Message}");
                    }

                    if (cached != null)
                    {
                        // Custom games keep the title the user gave them in the Xbox app
                        if (!GamePlatformHelper.IsCustomLibrary(game.Platform) && !string.IsNullOrEmpty(cached.Name))
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
    }
}
