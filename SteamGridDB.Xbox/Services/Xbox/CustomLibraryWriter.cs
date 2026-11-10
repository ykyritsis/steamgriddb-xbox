using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Windows.Data.Json;
using Windows.Storage;
using Windows.System;
using Windows.System.Diagnostics;

namespace SteamGridDB.Xbox.Services.Xbox
{
    /// <summary>
    /// A custom game entry to add to the Xbox app library.
    /// </summary>
    public class CustomGameEntry
    {
        public string Title { get; set; }
        public string InstallLocation { get; set; }
        public string ExecutableName { get; set; }
        public string ExecutableCommandArgs { get; set; }
        public string ImagePath { get; set; }
    }

    /// <summary>
    /// Adds entries to the Xbox app's custom library - the same manifest its "Add games to library" feature writes.
    /// </summary>
    /// <remarks>
    /// Format and pitfalls as documented by https://github.com/izzyjade06/xbox-rom-manager:
    /// - entries are keyed by a braced GUID and need id, title, installLocation, executableName,
    ///   executableCommandArgs, imagePath and addedDate;
    /// - every entry must have imagePath (an empty string is fine), otherwise the Xbox app ignores the whole custom library;
    /// - the Xbox app keeps the library in memory and saves over the file, so it must not be running while writing.
    /// </remarks>
    public static class CustomLibraryWriter
    {
        public const string XboxAppPackageFamilyName = "Microsoft.GamingApp_8wekyb3d8bbwe";

        private const string customFolderName = "CustomLibraryManagement";
        private const string backupFolderName = "CustomLibraryBackups";
        private const int backupsToKeep = 10;

        public static string LocalStatePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"AppData\Local\Packages", XboxAppPackageFamilyName, "LocalState");

        /// <summary>
        /// Folder where the Xbox app keeps images for custom library entries.
        /// </summary>
        public static async Task<StorageFolder> GetImagesFolderAsync()
        {
            StorageFolder localState = await StorageFolder.GetFolderFromPathAsync(LocalStatePath);
            StorageFolder customFolder = await localState.CreateFolderAsync(customFolderName, CreationCollisionOption.OpenIfExists);

            return await customFolder.CreateFolderAsync("Images", CreationCollisionOption.OpenIfExists);
        }

        /// <summary>
        /// Reads every custom entry's launch arguments, to find entries that already exist.
        /// </summary>
        public static async Task<List<string>> GetExistingLaunchArgumentsAsync()
        {
            JsonObject gameCache = GetGameCache(await ReadManifestAsync());

            return gameCache
                .Where(e => e.Key != "version" && e.Value.ValueType == JsonValueType.Object)
                .Select(e => e.Value.GetObject().GetNamedString("executableCommandArgs", string.Empty))
                .ToList();
        }

        /// <summary>
        /// Adds the entries to the manifest, backing the current file up first. Returns the IDs of the added entries.
        /// </summary>
        public static async Task<List<string>> AddEntriesAsync(IEnumerable<CustomGameEntry> entries)
        {
            StorageFolder folder = await GetManifestFolderAsync();
            JsonObject root = await ReadManifestAsync() ?? CreateEmptyManifest();

            await BackupManifestAsync(folder);

            if (!root.ContainsKey("gameCache") || root["gameCache"].ValueType != JsonValueType.Object)
            {
                root["gameCache"] = new JsonObject { ["version"] = JsonValue.CreateNumberValue(1) };
            }

            JsonObject gameCache = root.GetNamedObject("gameCache");
            var addedIds = new List<string>();

            foreach (CustomGameEntry entry in entries)
            {
                string id = "{" + Guid.NewGuid().ToString() + "}";

                gameCache[id] = new JsonObject
                {
                    ["id"] = JsonValue.CreateStringValue(id),
                    ["title"] = JsonValue.CreateStringValue(entry.Title),
                    ["installLocation"] = JsonValue.CreateStringValue(entry.InstallLocation),
                    ["executableName"] = JsonValue.CreateStringValue(entry.ExecutableName),
                    ["executableCommandArgs"] = JsonValue.CreateStringValue(entry.ExecutableCommandArgs ?? string.Empty),
                    ["imagePath"] = JsonValue.CreateStringValue(entry.ImagePath ?? string.Empty),
                    ["addedDate"] = JsonValue.CreateStringValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString())
                };

                addedIds.Add(id);
            }

            // One entry without imagePath makes the Xbox app drop the whole custom library
            foreach (KeyValuePair<string, IJsonValue> existing in gameCache)
            {
                if (existing.Value.ValueType == JsonValueType.Object && !existing.Value.GetObject().ContainsKey("imagePath"))
                {
                    existing.Value.GetObject()["imagePath"] = JsonValue.CreateStringValue(string.Empty);
                }
            }

            StorageFile manifest = await folder.CreateFileAsync($"{customFolderName}.manifest", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(manifest, root.Stringify());

            return addedIds;
        }

        /// <summary>
        /// Points an existing custom entry at a new image file, backing the manifest up first.
        /// </summary>
        /// <returns>False if no entry with that ID exists.</returns>
        /// <param name="apps">True for an entry in the custom apps list, false for the custom games list.</param>
        public static async Task<bool> SetImagePathAsync(string entryId, string imagePath, bool apps = false)
        {
            StorageFolder folder = await GetManifestFolderAsync(apps);
            JsonObject root = await ReadManifestAsync(apps);
            JsonObject entry = FindEntry(GetGameCache(root), entryId);

            if (entry == null)
            {
                return false;
            }

            await BackupManifestAsync(folder, apps);

            entry["imagePath"] = JsonValue.CreateStringValue(imagePath);

            StorageFile manifest = await folder.CreateFileAsync($"{customFolderName}.manifest", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(manifest, root.Stringify());

            return true;
        }

        /// <summary>
        /// Returns the image path currently stored for a custom entry, or null if the entry does not exist.
        /// </summary>
        public static async Task<string> GetImagePathAsync(string entryId, bool apps = false)
        {
            JsonObject entry = FindEntry(GetGameCache(await ReadManifestAsync(apps)), entryId);

            return entry?.GetNamedString("imagePath", string.Empty);
        }

        /// <summary>
        /// Finds an entry by key, or by its "id" field (entries the Xbox app creates use unbraced GUIDs, ours braced ones).
        /// </summary>
        private static JsonObject FindEntry(JsonObject gameCache, string entryId)
        {
            if (string.IsNullOrEmpty(entryId))
            {
                return null;
            }

            if (gameCache.TryGetValue(entryId, out IJsonValue byKey) && byKey.ValueType == JsonValueType.Object)
            {
                return byKey.GetObject();
            }

            return gameCache
                .Where(e => e.Value.ValueType == JsonValueType.Object)
                .Select(e => e.Value.GetObject())
                .FirstOrDefault(e => string.Equals(e.GetNamedString("id", string.Empty), entryId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns how many of the given entry IDs are currently in the manifest (to check the Xbox app did not save over them).
        /// </summary>
        public static async Task<int> CountEntriesAsync(IEnumerable<string> ids)
        {
            JsonObject gameCache = GetGameCache(await ReadManifestAsync());

            return ids.Count(id => gameCache.ContainsKey(id));
        }

        // Processes of the Xbox app's package that host Game Bar widgets rather than the app itself. They run whenever
        // Game Bar is open (so the user cannot close them) and, as tested, do not save over entries added to the library.
        private static readonly HashSet<string> gameBarHelperProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "XboxGameBarWidgets.exe",
            "XboxPcAppFT.exe"
        };

        /// <summary>
        /// Whether a process is part of the Xbox app proper (window, tray icon...), which must be closed before writing.
        /// Anything not known to be a Game Bar helper counts, so a renamed app process is never ignored by mistake.
        /// </summary>
        private static bool IsLibraryProcess(ProcessDiagnosticInfo process)
        {
            return !gameBarHelperProcesses.Contains(process.ExecutableFileName ?? string.Empty);
        }

        /// <summary>
        /// Number of running Xbox app processes that must be closed (Game Bar helpers excluded), or -1 when that cannot
        /// be determined (no diagnostic access).
        /// </summary>
        public static async Task<int> GetRunningProcessCountAsync()
        {
            try
            {
                if (await AppDiagnosticInfo.RequestAccessAsync() != DiagnosticAccessStatus.Allowed)
                {
                    return -1;
                }

                IList<AppDiagnosticInfo> infos = await AppDiagnosticInfo.RequestInfoForPackageAsync(XboxAppPackageFamilyName);

                return infos.SelectMany(i => i.GetResourceGroups()).Sum(g => g.GetProcessDiagnosticInfos().Count(IsLibraryProcess));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not check whether the Xbox app is running: {ex.Message}");

                return -1;
            }
        }

        /// <summary>
        /// Tries to close the Xbox app so it does not overwrite the manifest. Needs the user to allow diagnostic access.
        /// </summary>
        /// <returns>True when the Xbox app is not running afterwards.</returns>
        public static async Task<bool> TryCloseXboxAppAsync()
        {
            try
            {
                if (await AppDiagnosticInfo.RequestAccessAsync() != DiagnosticAccessStatus.Allowed)
                {
                    return false;
                }

                IList<AppDiagnosticInfo> infos = await AppDiagnosticInfo.RequestInfoForPackageAsync(XboxAppPackageFamilyName);

                foreach (AppDiagnosticInfo info in infos)
                {
                    foreach (AppResourceGroupInfo group in info.GetResourceGroups())
                    {
                        // Leave the Game Bar helpers alone: closing them would take Xbox's own widgets down with them
                        if (!group.GetProcessDiagnosticInfos().Any(IsLibraryProcess))
                        {
                            continue;
                        }

                        AppExecutionStateChangeResult result = await group.StartTerminateAsync();

                        if (result.ExtendedError != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Could not close part of the Xbox app: {result.ExtendedError.Message}");
                        }
                    }
                }

                // A successful terminate request does not mean the processes are gone. The Xbox app runs several
                // (XboxPcAppCE, XboxPcTray...) and any one left running can save over the library, so only report
                // success once none are left.
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (await GetRunningProcessCountAsync() == 0)
                    {
                        return true;
                    }

                    await Task.Delay(500);
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not close the Xbox app: {ex.Message}");

                return false;
            }
        }

        /// <summary>
        /// Folder holding the custom apps list (the Xbox app's "Apps" tab). It is separate from the custom games list,
        /// which lives under ThirdPartyLibraries, but uses the same format and the same images folder.
        /// </summary>
        public static async Task<StorageFolder> GetAppsFolderAsync()
        {
            return await GetManifestFolderAsync(true);
        }

        private static async Task<StorageFolder> GetManifestFolderAsync(bool apps = false)
        {
            StorageFolder localState = await StorageFolder.GetFolderFromPathAsync(LocalStatePath);

            if (apps)
            {
                return await localState.CreateFolderAsync(customFolderName, CreationCollisionOption.OpenIfExists);
            }

            StorageFolder thirdParty = await localState.CreateFolderAsync("ThirdPartyLibraries", CreationCollisionOption.OpenIfExists);

            return await thirdParty.CreateFolderAsync(customFolderName, CreationCollisionOption.OpenIfExists);
        }

        private static async Task<JsonObject> ReadManifestAsync(bool apps = false)
        {
            StorageFolder folder = await GetManifestFolderAsync(apps);

            if (await folder.TryGetItemAsync($"{customFolderName}.manifest") is StorageFile file)
            {
                string json = await FileIO.ReadTextAsync(file);

                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                if (JsonObject.TryParse(json, out JsonObject root))
                {
                    return root;
                }

                // Never overwrite a manifest we cannot understand
                throw new InvalidOperationException("The Xbox app's custom library file could not be read. Try again in a moment.");
            }

            return null;
        }

        private static JsonObject GetGameCache(JsonObject root)
        {
            if (root != null && root.TryGetValue("gameCache", out IJsonValue value) && value.ValueType == JsonValueType.Object)
            {
                return value.GetObject();
            }

            return new JsonObject();
        }

        private static JsonObject CreateEmptyManifest()
        {
            return new JsonObject
            {
                ["version"] = JsonValue.CreateNumberValue(1),
                ["provider"] = new JsonObject
                {
                    ["version"] = JsonValue.CreateNumberValue(1),
                    ["enabled"] = JsonValue.CreateBooleanValue(true)
                },
                ["gameCache"] = new JsonObject { ["version"] = JsonValue.CreateNumberValue(1) }
            };
        }

        /// <summary>
        /// Copies the current manifest into the widget's own storage, keeping the last few copies.
        /// </summary>
        private static async Task BackupManifestAsync(StorageFolder manifestFolder, bool apps = false)
        {
            if (!(await manifestFolder.TryGetItemAsync($"{customFolderName}.manifest") is StorageFile manifest))
            {
                return;
            }

            StorageFolder backups = await ApplicationData.Current.LocalFolder.CreateFolderAsync(backupFolderName, CreationCollisionOption.OpenIfExists);
            string prefix = apps ? "CustomApps" : customFolderName;

            await manifest.CopyAsync(backups, $"{prefix}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.manifest", NameCollisionOption.ReplaceExisting);

            IReadOnlyList<StorageFile> existing = await backups.GetFilesAsync();

            foreach (StorageFile old in existing.Where(f => f.Name.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.Name).Skip(backupsToKeep))
            {
                await old.DeleteAsync();
            }
        }
    }
}
