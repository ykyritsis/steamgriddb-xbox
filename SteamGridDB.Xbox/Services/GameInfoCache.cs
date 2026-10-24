using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Windows.Data.Json;
using Windows.Storage;

namespace SteamGridDB.Xbox.Services
{
    /// <summary>
    /// Resolved game name and SteamGridDB match for one store game.
    /// </summary>
    public class CachedGameInfo
    {
        public string Name { get; set; }
        public int? SteamGridDbGameId { get; set; }
        public bool HasSteamGridDBMatch { get; set; }

        /// <summary>
        /// Whether the lookup was made with a SteamGridDB API key (results without one are incomplete).
        /// </summary>
        public bool ResolvedWithSteamGridDb { get; set; }

        public DateTimeOffset ResolvedAt { get; set; }
    }

    /// <summary>
    /// Persists game lookups between widget sessions so opening the widget, and every automatic reload, does not
    /// repeat a network round trip per game.
    /// </summary>
    /// <remarks>
    /// Matches are kept for a month. Games without a match are retried after a day, because SteamGridDB and store
    /// databases catch up with new releases over time.
    /// </remarks>
    public static class GameInfoCache
    {
        private const string cacheFileName = "game-info-cache.json";
        private static readonly TimeSpan matchedLifetime = TimeSpan.FromDays(30);
        private static readonly TimeSpan unmatchedLifetime = TimeSpan.FromDays(1);

        private static Dictionary<string, CachedGameInfo> entries;
        private static bool dirty;

        public static string GetKey(string platform, string externalPlatformId) => $"{platform}:{externalPlatformId}";

        /// <summary>
        /// Returns a fresh cached entry, or null if there is none or it has expired or was resolved without SteamGridDB
        /// while a key is now available.
        /// </summary>
        public static async Task<CachedGameInfo> TryGetAsync(string key, bool steamGridDbAvailable)
        {
            await EnsureLoadedAsync();

            if (!entries.TryGetValue(key, out CachedGameInfo info))
            {
                return null;
            }

            TimeSpan lifetime = info.HasSteamGridDBMatch ? matchedLifetime : unmatchedLifetime;

            if (DateTimeOffset.UtcNow - info.ResolvedAt > lifetime || (steamGridDbAvailable && !info.ResolvedWithSteamGridDb))
            {
                return null;
            }

            return info;
        }

        public static async Task SetAsync(string key, CachedGameInfo info)
        {
            await EnsureLoadedAsync();

            entries[key] = info;
            dirty = true;
        }

        /// <summary>
        /// Drops every entry, e.g. when the API key changes.
        /// </summary>
        public static async Task ClearAsync()
        {
            await EnsureLoadedAsync();

            entries.Clear();
            dirty = true;

            await SaveAsync();
        }

        /// <summary>
        /// Writes the cache to disk if anything changed since the last save.
        /// </summary>
        public static async Task SaveAsync()
        {
            if (!dirty || entries == null)
            {
                return;
            }

            try
            {
                JsonObject root = new JsonObject();

                foreach (KeyValuePair<string, CachedGameInfo> entry in entries)
                {
                    JsonObject item = new JsonObject
                    {
                        ["name"] = JsonValue.CreateStringValue(entry.Value.Name ?? string.Empty),
                        ["matched"] = JsonValue.CreateBooleanValue(entry.Value.HasSteamGridDBMatch),
                        ["withSgdb"] = JsonValue.CreateBooleanValue(entry.Value.ResolvedWithSteamGridDb),
                        ["at"] = JsonValue.CreateNumberValue(entry.Value.ResolvedAt.ToUnixTimeSeconds())
                    };

                    if (entry.Value.SteamGridDbGameId.HasValue)
                    {
                        item["sgdbId"] = JsonValue.CreateNumberValue(entry.Value.SteamGridDbGameId.Value);
                    }

                    root[entry.Key] = item;
                }

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(cacheFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, root.Stringify());

                dirty = false;
            }
            catch (Exception ex)
            {
                // The cache is an optimisation only
                System.Diagnostics.Debug.WriteLine($"Could not save game info cache: {ex.Message}");
            }
        }

        private static async Task EnsureLoadedAsync()
        {
            if (entries != null)
            {
                return;
            }

            Dictionary<string, CachedGameInfo> loaded = new Dictionary<string, CachedGameInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (await ApplicationData.Current.LocalFolder.TryGetItemAsync(cacheFileName) is StorageFile file &&
                    JsonObject.TryParse(await FileIO.ReadTextAsync(file), out JsonObject root))
                {
                    foreach (KeyValuePair<string, IJsonValue> entry in root)
                    {
                        if (entry.Value.ValueType != JsonValueType.Object)
                        {
                            continue;
                        }

                        JsonObject item = entry.Value.GetObject();

                        loaded[entry.Key] = new CachedGameInfo
                        {
                            Name = item.GetNamedString("name", string.Empty),
                            HasSteamGridDBMatch = item.GetNamedBoolean("matched", false),
                            ResolvedWithSteamGridDb = item.GetNamedBoolean("withSgdb", false),
                            SteamGridDbGameId = item.ContainsKey("sgdbId") ? (int?)item.GetNamedNumber("sgdbId") : null,
                            ResolvedAt = DateTimeOffset.FromUnixTimeSeconds((long)item.GetNamedNumber("at", 0))
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                // A corrupt cache is simply rebuilt
                System.Diagnostics.Debug.WriteLine($"Could not load game info cache: {ex.Message}");
            }

            // Another caller may have finished loading while this one was awaiting
            if (entries == null)
            {
                entries = loaded;
            }
        }
    }
}
