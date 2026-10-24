using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Windows.Data.Json;
using Windows.Storage;
using Windows.System;

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
        /// Returns how many of the given entry IDs are currently in the manifest (to check the Xbox app did not save over them).
        /// </summary>
        public static async Task<int> CountEntriesAsync(IEnumerable<string> ids)
        {
            JsonObject gameCache = GetGameCache(await ReadManifestAsync());

            return ids.Count(id => gameCache.ContainsKey(id));
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
                bool allClosed = true;

                foreach (AppDiagnosticInfo info in infos)
                {
                    foreach (AppResourceGroupInfo group in info.GetResourceGroups())
                    {
                        if (group.GetProcessDiagnosticInfos().Count == 0)
                        {
                            continue;
                        }

                        AppExecutionStateChangeResult result = await group.StartTerminateAsync();

                        if (result.ExtendedError != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Could not close the Xbox app: {result.ExtendedError.Message}");
                            allClosed = false;
                        }
                    }
                }

                return allClosed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not close the Xbox app: {ex.Message}");

                return false;
            }
        }

        private static async Task<StorageFolder> GetManifestFolderAsync()
        {
            StorageFolder localState = await StorageFolder.GetFolderFromPathAsync(LocalStatePath);
            StorageFolder thirdParty = await localState.CreateFolderAsync("ThirdPartyLibraries", CreationCollisionOption.OpenIfExists);

            return await thirdParty.CreateFolderAsync(customFolderName, CreationCollisionOption.OpenIfExists);
        }

        private static async Task<JsonObject> ReadManifestAsync()
        {
            StorageFolder folder = await GetManifestFolderAsync();

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
        private static async Task BackupManifestAsync(StorageFolder manifestFolder)
        {
            if (!(await manifestFolder.TryGetItemAsync($"{customFolderName}.manifest") is StorageFile manifest))
            {
                return;
            }

            StorageFolder backups = await ApplicationData.Current.LocalFolder.CreateFolderAsync(backupFolderName, CreationCollisionOption.OpenIfExists);

            await manifest.CopyAsync(backups, $"{customFolderName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.manifest", NameCollisionOption.ReplaceExisting);

            IReadOnlyList<StorageFile> existing = await backups.GetFilesAsync();

            foreach (StorageFile old in existing.OrderByDescending(f => f.Name).Skip(backupsToKeep))
            {
                await old.DeleteAsync();
            }
        }
    }
}
