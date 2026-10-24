using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Windows.Storage;

namespace SteamGridDB.Xbox.Services.Steam
{
    /// <summary>
    /// A game installed through Steam.
    /// </summary>
    public class InstalledSteamGame
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public string LibraryPath { get; set; }
    }

    /// <summary>
    /// Finds games installed by Steam by reading its library files (libraryfolders.vdf and appmanifest_*.acf).
    /// </summary>
    public static class SteamLibraryScanner
    {
        // StateFlags bit meaning "fully installed"
        private const int fullyInstalledFlag = 4;

        // Steam tools that are installed like games but are not something to launch
        private static readonly HashSet<string> nonGameAppIds = new HashSet<string>
        {
            "228980",   // Steamworks Common Redistributables
            "1070560",  // Steam Linux Runtime
            "1391110",  // Steam Linux Runtime - Soldier
            "1628350",  // Steam Linux Runtime - Sniper
            "250820",   // SteamVR
            "1493710"   // Proton Experimental
        };

        private static readonly Regex nonGameNamePattern = new Regex(@"^(Proton\b|Steam Linux Runtime|Steamworks\b|SteamVR\b)", RegexOptions.IgnoreCase);

        /// <summary>
        /// Default Steam install locations, checked in order when no folder is configured.
        /// </summary>
        public static readonly string[] DefaultSteamPaths =
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        };

        /// <summary>
        /// Returns the Steam folder (containing steam.exe), or null if Steam cannot be found.
        /// </summary>
        public static async Task<StorageFolder> FindSteamFolderAsync(string configuredPath)
        {
            IEnumerable<string> candidates = string.IsNullOrWhiteSpace(configuredPath)
                ? DefaultSteamPaths
                : new[] { configuredPath.Trim() }.Concat(DefaultSteamPaths);

            foreach (string path in candidates)
            {
                try
                {
                    StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(path);

                    if (await folder.TryGetItemAsync("steam.exe") != null)
                    {
                        return folder;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Steam not found at {path}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Lists fully installed games across every Steam library folder.
        /// </summary>
        public static async Task<List<InstalledSteamGame>> GetInstalledGamesAsync(StorageFolder steamFolder)
        {
            var games = new Dictionary<string, InstalledSteamGame>();
            var libraryPaths = new List<string> { steamFolder.Path };

            try
            {
                StorageFolder steamApps = await steamFolder.GetFolderAsync("steamapps");

                if (await steamApps.TryGetItemAsync("libraryfolders.vdf") is StorageFile libraryFile)
                {
                    string vdf = await FileIO.ReadTextAsync(libraryFile);

                    foreach (Match match in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string path = match.Groups[1].Value.Replace(@"\\", @"\");

                        if (!libraryPaths.Any(p => string.Equals(p.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                        {
                            libraryPaths.Add(path);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not read Steam library folders: {ex.Message}");
            }

            foreach (string libraryPath in libraryPaths)
            {
                try
                {
                    StorageFolder steamApps = await StorageFolder.GetFolderFromPathAsync(Path.Combine(libraryPath, "steamapps"));

                    foreach (StorageFile manifest in await steamApps.GetFilesAsync())
                    {
                        if (!manifest.Name.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) ||
                            !manifest.Name.EndsWith(".acf", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        InstalledSteamGame game = ParseAppManifest(await FileIO.ReadTextAsync(manifest), libraryPath);

                        if (game != null && !games.ContainsKey(game.AppId))
                        {
                            games[game.AppId] = game;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // A library on a disconnected drive must not hide the others
                    System.Diagnostics.Debug.WriteLine($"Could not read Steam library {libraryPath}: {ex.Message}");
                }
            }

            return games.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static InstalledSteamGame ParseAppManifest(string acf, string libraryPath)
        {
            string appId = GetVdfValue(acf, "appid");
            string name = GetVdfValue(acf, "name");

            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name) || nonGameAppIds.Contains(appId) || nonGameNamePattern.IsMatch(name))
            {
                return null;
            }

            // Skip games that are still downloading or were only partly installed
            if (int.TryParse(GetVdfValue(acf, "StateFlags"), out int stateFlags) && (stateFlags & fullyInstalledFlag) == 0)
            {
                return null;
            }

            return new InstalledSteamGame
            {
                AppId = appId,
                Name = name.Replace("™", string.Empty).Replace("®", string.Empty).Trim(),
                LibraryPath = libraryPath
            };
        }

        private static string GetVdfValue(string vdf, string key)
        {
            Match match = Regex.Match(vdf, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
