using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Windows.Data.Json;
using Windows.Web.Http;

namespace SteamGridDB.Xbox.Services.Stores
{
    /// <summary>
    /// Square artwork found on a store or media site.
    /// </summary>
    public class StoreArtwork
    {
        public string Source { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string ThumbUrl { get; set; }

        /// <summary>
        /// How closely the title matches the searched name (1 = exact match after normalisation).
        /// </summary>
        public double MatchScore { get; set; }
    }

    /// <summary>
    /// Fetches square cover art from the Xbox Store, PlayStation Store and IGN.
    /// The endpoints are the same public ones used by the Playnite XboxMetadata and IgnMetadata extensions
    /// (https://github.com/Jeshibu/PlayniteExtensions) and the PlayStation Store website.
    /// </summary>
    public static class StoreArtworkClient
    {
        public const string XboxSource = "Xbox Store";
        public const string PlayStationSource = "PlayStation Store";
        public const string IgnSource = "IGN";

        private const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

        // Persisted query hashes - these are the IDs the official websites send for these operations
        private const string psnSearchHash = "4df6284f982e57bec70f23c77e2c219dc792eb19af7fb3d3a81767aa3f1958aa";
        private const string ignSearchHash = "e1c2e012a21b4a98aaa618ef1b43eb0cafe9136303274a34f5d9ea4f2446e884";

        // PlayStation Store classifications that are not the game itself
        private static readonly HashSet<string> psnExcludedClassifications = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Add-on", "Level", "Avatar", "Theme", "Virtual Currency", "Season Pass", "Character", "Costume", "Map", "Vehicle", "Item"
        };

        private static readonly HttpClient httpClient = new HttpClient();

        /// <summary>
        /// Searches all stores in parallel. Failures in one store never affect the others.
        /// </summary>
        /// <param name="gameName">Game name to search for.</param>
        /// <param name="maxPerSource">Maximum number of artworks returned from each store.</param>
        public static async Task<List<StoreArtwork>> SearchAllAsync(string gameName, int maxPerSource = 4)
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return new List<StoreArtwork>();
            }

            // Reopening the artwork panel, or "Fix my library" after browsing, often repeats the same search
            string cacheKey = $"{NormaliseTitle(gameName)}|{maxPerSource}";

            if (searchCache.TryGetValue(cacheKey, out List<StoreArtwork> cached))
            {
                return cached.ToList();
            }

            Task<List<StoreArtwork>>[] tasks =
            {
                SafeSearchAsync(SearchXboxAsync, gameName, maxPerSource),
                SafeSearchAsync(SearchPlayStationAsync, gameName, maxPerSource),
                SafeSearchAsync(SearchIgnAsync, gameName, maxPerSource)
            };

            List<StoreArtwork>[] results = await Task.WhenAll(tasks);
            List<StoreArtwork> all = results.SelectMany(r => r).ToList();

            // Empty results are not cached: they are usually a temporary network failure
            if (all.Count > 0)
            {
                searchCache[cacheKey] = all;
            }

            return all.ToList();
        }

        // Session-lifetime cache of store searches, keyed by normalised name and result count
        private static readonly ConcurrentDictionary<string, List<StoreArtwork>> searchCache = new ConcurrentDictionary<string, List<StoreArtwork>>();

        /// <summary>
        /// Returns the best artwork whose title is an exact (normalised) match for the game name, or null.
        /// Used for automatic fixing, where a wrong match is worse than no match.
        /// </summary>
        public static async Task<StoreArtwork> FindExactMatchAsync(string gameName)
        {
            List<StoreArtwork> all = await SearchAllAsync(gameName, 2);

            // Preference order: Xbox box art, then PlayStation master art, then IGN
            return all.Where(a => a.MatchScore >= 1.0)
                      .OrderBy(a => a.Source == XboxSource ? 0 : a.Source == PlayStationSource ? 1 : 2)
                      .FirstOrDefault();
        }

        private static async Task<List<StoreArtwork>> SafeSearchAsync(Func<string, int, Task<List<StoreArtwork>>> search, string gameName, int maxPerSource)
        {
            try
            {
                return await search(gameName, maxPerSource);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Store artwork search failed for '{gameName}': {ex.Message}");

                return new List<StoreArtwork>();
            }
        }

        #region Xbox Store

        public static async Task<List<StoreArtwork>> SearchXboxAsync(string gameName, int max)
        {
            var output = new List<StoreArtwork>();
            string market = GetMarket();

            // Colons and other symbols make the autosuggest endpoint return nothing
            string query = Uri.EscapeDataString(StripSymbols(gameName));
            string searchUrl = $"https://www.microsoft.com/msstoreapiprod/api/autosuggest?market={market}&sources=DCatAll-Products,xSearch-Products&filter=+ClientType:StoreWeb&counts=20,20&query={query}";

            JsonObject searchRoot = await GetJsonAsync(searchUrl);

            if (searchRoot == null)
            {
                return output;
            }

            var ids = new List<string>();

            foreach (IJsonValue resultSet in GetArray(searchRoot, "ResultSets"))
            {
                if (resultSet.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                foreach (IJsonValue suggest in GetArray(resultSet.GetObject(), "Suggests"))
                {
                    if (suggest.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    JsonObject suggestObject = suggest.GetObject();

                    if (GetString(suggestObject, "Source") != "Game")
                    {
                        continue;
                    }

                    foreach (IJsonValue meta in GetArray(suggestObject, "Metas"))
                    {
                        if (meta.ValueType == JsonValueType.Object && GetString(meta.GetObject(), "Key") == "BigCatalogId")
                        {
                            string id = GetString(meta.GetObject(), "Value");

                            if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
                            {
                                ids.Add(id);
                            }
                        }
                    }
                }
            }

            if (ids.Count == 0)
            {
                return output;
            }

            string catalogUrl = $"https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds={string.Join(",", ids.Take(10))}&market={market.Substring(market.Length - 2).ToUpperInvariant()}&languages={market}";
            JsonObject catalogRoot = await GetJsonAsync(catalogUrl);

            if (catalogRoot == null)
            {
                return output;
            }

            foreach (IJsonValue product in GetArray(catalogRoot, "Products"))
            {
                if (product.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                JsonArray localizedProperties = GetArray(product.GetObject(), "LocalizedProperties");

                if (localizedProperties.Count == 0 || localizedProperties[0].ValueType != JsonValueType.Object)
                {
                    continue;
                }

                JsonObject properties = localizedProperties[0].GetObject();
                string title = GetString(properties, "ProductTitle");

                // BoxArt is the square cover the Xbox app itself uses; fall back to other square promotional art
                JsonObject bestImage = GetArray(properties, "Images")
                    .Where(i => i.ValueType == JsonValueType.Object)
                    .Select(i => i.GetObject())
                    .Where(i => GetNumber(i, "Width") > 0 && GetNumber(i, "Width") == GetNumber(i, "Height"))
                    .OrderBy(i => GetString(i, "ImagePurpose") == "BoxArt" ? 0 : GetString(i, "ImagePurpose") == "FeaturePromotionalSquareArt" ? 1 : 2)
                    .ThenByDescending(i => GetNumber(i, "Width"))
                    .FirstOrDefault(i => GetString(i, "ImagePurpose") != "Logo" || GetNumber(i, "Width") >= 512);

                if (bestImage == null)
                {
                    continue;
                }

                string uri = GetString(bestImage, "Uri");

                if (string.IsNullOrEmpty(uri))
                {
                    continue;
                }

                if (uri.StartsWith("//"))
                {
                    uri = "https:" + uri;
                }

                output.Add(new StoreArtwork
                {
                    Source = XboxSource,
                    Title = title,
                    Url = uri + "?w=1024&h=1024",
                    ThumbUrl = uri + "?w=256&h=256",
                    MatchScore = TitleMatchScore(gameName, title)
                });
            }

            return output.OrderByDescending(a => a.MatchScore).Take(max).ToList();
        }

        #endregion

        #region PlayStation Store

        public static async Task<List<StoreArtwork>> SearchPlayStationAsync(string gameName, int max)
        {
            var output = new List<StoreArtwork>();
            string[] locale = GetMarket().Split('-');

            var variables = new JsonObject
            {
                ["countryCode"] = JsonValue.CreateStringValue(locale[1].ToUpperInvariant()),
                ["languageCode"] = JsonValue.CreateStringValue(locale[0]),
                ["nextCursor"] = JsonValue.CreateStringValue(string.Empty),
                ["pageOffset"] = JsonValue.CreateNumberValue(0),
                ["pageSize"] = JsonValue.CreateNumberValue(24),
                ["searchTerm"] = JsonValue.CreateStringValue(gameName)
            };

            string url = BuildPersistedQueryUrl("https://web.np.playstation.com/api/graphql/v1/op", "getSearchResults", variables, psnSearchHash);

            JsonObject root = await GetJsonAsync(url, new Dictionary<string, string>
            {
                // Required by the endpoint's CSRF protection
                ["x-apollo-operation-name"] = "getSearchResults"
            });

            JsonObject search = GetObject(GetObject(root, "data"), "universalSearch");

            foreach (IJsonValue result in GetArray(search, "results"))
            {
                if (result.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                JsonObject product = result.GetObject();

                if (psnExcludedClassifications.Contains(GetString(product, "localizedStoreDisplayClassification") ?? string.Empty))
                {
                    continue;
                }

                // MASTER is the square key art shown as the game tile on the console
                string imageUrl = GetArray(product, "media")
                    .Where(m => m.ValueType == JsonValueType.Object)
                    .Select(m => m.GetObject())
                    .Where(m => GetString(m, "type") == "IMAGE" && GetString(m, "role") == "MASTER")
                    .Select(m => GetString(m, "url"))
                    .FirstOrDefault(u => !string.IsNullOrEmpty(u));

                if (imageUrl == null)
                {
                    continue;
                }

                string title = GetString(product, "name");

                output.Add(new StoreArtwork
                {
                    Source = PlayStationSource,
                    Title = title,
                    Url = imageUrl + "?w=1024",
                    ThumbUrl = imageUrl + "?w=256",
                    MatchScore = TitleMatchScore(gameName, title)
                });
            }

            // The same game is often listed once per console generation - keep one of each image
            return output.GroupBy(a => a.Url)
                         .Select(g => g.First())
                         .OrderByDescending(a => a.MatchScore)
                         .Take(max)
                         .ToList();
        }

        #endregion

        #region IGN

        public static async Task<List<StoreArtwork>> SearchIgnAsync(string gameName, int max)
        {
            var output = new List<StoreArtwork>();

            var variables = new JsonObject
            {
                ["term"] = JsonValue.CreateStringValue(gameName),
                ["count"] = JsonValue.CreateNumberValue(10),
                ["objectType"] = JsonValue.CreateStringValue("Game")
            };

            string url = BuildPersistedQueryUrl("https://mollusk.apis.ign.com/graphql", "SearchObjectsByName", variables, ignSearchHash);

            JsonObject root = await GetJsonAsync(url, new Dictionary<string, string>
            {
                ["apollographql-client-name"] = "kraken",
                ["apollographql-client-version"] = "v0.98.36",
                ["x-apollo-operation-name"] = "SearchObjectsByName"
            });

            JsonObject search = GetObject(GetObject(root, "data"), "searchObjectsByName");

            foreach (IJsonValue result in GetArray(search, "objects"))
            {
                if (result.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                JsonObject game = result.GetObject();
                string imageUrl = GetString(GetObject(game, "primaryImage"), "url");

                if (string.IsNullOrEmpty(imageUrl))
                {
                    continue;
                }

                string title = GetString(GetObject(GetObject(game, "metadata"), "names"), "name");

                // IGN's image service can crop to a square on the fly
                output.Add(new StoreArtwork
                {
                    Source = IgnSource,
                    Title = title,
                    Url = imageUrl + "?width=1024&crop=1%3A1",
                    ThumbUrl = imageUrl + "?width=256&crop=1%3A1",
                    MatchScore = TitleMatchScore(gameName, title)
                });
            }

            return output.OrderByDescending(a => a.MatchScore).Take(max).ToList();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Normalises a title for comparison: lower case, no trademark symbols or punctuation, collapsed spaces.
        /// </summary>
        public static string NormaliseTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                return string.Empty;
            }

            string lower = title.ToLowerInvariant()
                .Replace("™", string.Empty)
                .Replace("®", string.Empty)
                .Replace("©", string.Empty)
                .Replace("&", " and ");

            var builder = new StringBuilder();

            foreach (char c in lower.Normalize(NormalizationForm.FormD))
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);

                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }

            return string.Join(" ", builder.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// 1.0 for an exact normalised match, otherwise the share of words in common.
        /// </summary>
        public static double TitleMatchScore(string searched, string candidate)
        {
            string a = NormaliseTitle(searched);
            string b = NormaliseTitle(candidate);

            if (a.Length == 0 || b.Length == 0)
            {
                return 0;
            }

            if (a == b)
            {
                return 1.0;
            }

            HashSet<string> wordsA = new HashSet<string>(a.Split(' '));
            HashSet<string> wordsB = new HashSet<string>(b.Split(' '));
            int common = wordsA.Count(w => wordsB.Contains(w));

            // Never return 1.0 for a partial match
            return Math.Min(0.99, (double)common / Math.Max(wordsA.Count, wordsB.Count));
        }

        private static string StripSymbols(string text)
        {
            var builder = new StringBuilder();

            foreach (char c in text)
            {
                builder.Append(char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ');
            }

            return string.Join(" ", builder.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string GetMarket()
        {
            // Store APIs want "en-us" style markets; fall back to en-us for neutral cultures
            string name = CultureInfo.CurrentUICulture.Name;

            if (string.IsNullOrEmpty(name) || !name.Contains("-"))
            {
                return "en-us";
            }

            string[] parts = name.Split('-');

            return $"{parts[0]}-{parts[parts.Length - 1]}".ToLowerInvariant();
        }

        private static string BuildPersistedQueryUrl(string endpoint, string operationName, JsonObject variables, string hash)
        {
            var persistedQuery = new JsonObject
            {
                ["version"] = JsonValue.CreateNumberValue(1),
                ["sha256Hash"] = JsonValue.CreateStringValue(hash)
            };
            var extensions = new JsonObject { ["persistedQuery"] = persistedQuery };

            return $"{endpoint}?operationName={operationName}&variables={Uri.EscapeDataString(variables.Stringify())}&extensions={Uri.EscapeDataString(extensions.Stringify())}";
        }

        private static async Task<JsonObject> GetJsonAsync(string url, IDictionary<string, string> headers = null)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url)))
            {
                request.Headers.TryAppendWithoutValidation("User-Agent", userAgent);
                request.Headers.TryAppendWithoutValidation("Accept", "application/json");

                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> header in headers)
                    {
                        request.Headers.TryAppendWithoutValidation(header.Key, header.Value);
                    }
                }

                using (HttpResponseMessage response = await httpClient.SendRequestAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine($"Store request failed ({response.StatusCode}): {url}");

                        return null;
                    }

                    string content = await response.Content.ReadAsStringAsync();

                    return JsonObject.TryParse(content, out JsonObject root) ? root : null;
                }
            }
        }

        private static JsonObject GetObject(JsonObject obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out IJsonValue value) && value.ValueType == JsonValueType.Object)
            {
                return value.GetObject();
            }

            return null;
        }

        private static JsonArray GetArray(JsonObject obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out IJsonValue value) && value.ValueType == JsonValueType.Array)
            {
                return value.GetArray();
            }

            return new JsonArray();
        }

        private static string GetString(JsonObject obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out IJsonValue value) && value.ValueType == JsonValueType.String)
            {
                return value.GetString();
            }

            return null;
        }

        private static double GetNumber(JsonObject obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out IJsonValue value) && value.ValueType == JsonValueType.Number)
            {
                return value.GetNumber();
            }

            return 0;
        }

        #endregion
    }
}
