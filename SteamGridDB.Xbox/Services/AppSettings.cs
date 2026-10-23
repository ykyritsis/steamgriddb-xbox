using System;

using Windows.Storage;

namespace SteamGridDB.Xbox.Services
{
    /// <summary>
    /// User settings persisted in the app's local settings.
    /// </summary>
    public static class AppSettings
    {
        private const string apiKeySetting = "SteamGridDbApiKey";
        private const string lockArtworkSetting = "LockArtwork";
        private const string storeSourcesSetting = "UseStoreSources";

        /// <summary>
        /// SteamGridDB API key entered in the widget settings, falling back to the STEAMGRIDDB_API_KEY environment variable.
        /// </summary>
        public static string SteamGridDbApiKey
        {
            get
            {
                string key = ApplicationData.Current.LocalSettings.Values[apiKeySetting] as string;

                if (string.IsNullOrWhiteSpace(key))
                {
                    key = Environment.GetEnvironmentVariable("STEAMGRIDDB_API_KEY");
                }

                return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            }
            set => ApplicationData.Current.LocalSettings.Values[apiKeySetting] = value?.Trim() ?? string.Empty;
        }

        /// <summary>
        /// Whether customised artwork is made read-only so the Xbox app cannot overwrite it. On by default.
        /// </summary>
        public static bool LockArtwork
        {
            get => !(ApplicationData.Current.LocalSettings.Values[lockArtworkSetting] is bool value) || value;
            set => ApplicationData.Current.LocalSettings.Values[lockArtworkSetting] = value;
        }

        /// <summary>
        /// Whether Xbox Store, PlayStation Store and IGN artwork is offered alongside SteamGridDB. On by default.
        /// </summary>
        public static bool UseStoreSources
        {
            get => !(ApplicationData.Current.LocalSettings.Values[storeSourcesSetting] is bool value) || value;
            set => ApplicationData.Current.LocalSettings.Values[storeSourcesSetting] = value;
        }
    }
}
