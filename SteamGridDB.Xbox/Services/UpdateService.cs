using System;
using System.Threading.Tasks;

using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace SteamGridDB.Xbox.Services
{
    /// <summary>
    /// A newer release of the widget.
    /// </summary>
    public class UpdateInfo
    {
        public Version Version { get; set; }
        public Uri PackageUri { get; set; }

        public string DisplayVersion => $"{Version.Major}.{Version.Minor}.{Version.Build}";
    }

    /// <summary>
    /// Finds newer releases on GitHub and installs them.
    /// </summary>
    /// <remarks>
    /// The widget is sideloaded, so neither the Store nor Windows updates it. Each release publishes the signed package
    /// for every CPU type; this downloads the matching one and hands it to Windows' package manager. Windows only
    /// accepts a package with the same identity and publisher certificate as the installed one, so a download that
    /// was tampered with cannot be installed.
    /// </remarks>
    public static class UpdateService
    {
        private const string repository = "ykyritsis/steamgriddb-xbox";
        private const string latestReleaseUrl = "https://api.github.com/repos/" + repository + "/releases/latest";
        private const string downloadUrlPrefix = "https://github.com/" + repository + "/releases/download/";
        private const string userAgent = "SteamGridDB-Xbox-Community";
        private const string packageFileName = "update.msix";

        public static Version CurrentVersion
        {
            get
            {
                PackageVersion version = Package.Current.Id.Version;

                return new Version(version.Major, version.Minor, version.Build, version.Revision);
            }
        }

        /// <summary>
        /// Returns the latest release if it is newer than the installed version and has a package for this CPU type;
        /// otherwise null (also when GitHub cannot be reached).
        /// </summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            try
            {
                JsonObject release;

                using (HttpClient client = CreateClient())
                using (HttpResponseMessage response = await client.GetAsync(new Uri(latestReleaseUrl)))
                {
                    if (!response.IsSuccessStatusCode || !JsonObject.TryParse(await response.Content.ReadAsStringAsync(), out release))
                    {
                        return null;
                    }
                }

                // Tags look like v1.7.11; package versions have four parts
                if (!Version.TryParse(release.GetNamedString("tag_name", string.Empty).TrimStart('v', 'V'), out Version tag))
                {
                    return null;
                }

                Version latest = new Version(tag.Major, tag.Minor, Math.Max(tag.Build, 0), Math.Max(tag.Revision, 0));

                if (latest <= CurrentVersion)
                {
                    return null;
                }

                string suffix = Package.Current.Id.Architecture == Windows.System.ProcessorArchitecture.Arm64 ? "_ARM64.msix" : "_x64.msix";

                foreach (IJsonValue value in release.GetNamedArray("assets", new JsonArray()))
                {
                    if (value.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    JsonObject asset = value.GetObject();
                    string url = asset.GetNamedString("browser_download_url", string.Empty);

                    // Only ever download from this repository's own releases
                    if (asset.GetNamedString("name", string.Empty).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                        url.StartsWith(downloadUrlPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return new UpdateInfo { Version = latest, PackageUri = new Uri(url) };
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Downloads and installs the update. Windows closes the widget to replace it, so on success this normally does
        /// not return; a returned string is the reason it failed.
        /// </summary>
        public static async Task<string> InstallAsync(UpdateInfo update)
        {
            try
            {
                StorageFile file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(packageFileName, CreationCollisionOption.ReplaceExisting);

                using (HttpClient client = CreateClient())
                using (HttpResponseMessage response = await client.GetAsync(update.PackageUri))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return $"the download failed ({(int)response.StatusCode})";
                    }

                    IBuffer package = await response.Content.ReadAsBufferAsync();
                    await FileIO.WriteBufferAsync(file, package);
                }

                DeploymentResult result = await new PackageManager().AddPackageAsync(new Uri(file.Path), null, DeploymentOptions.ForceApplicationShutdown);

                return result.ExtendedErrorCode == null ? null : (string.IsNullOrEmpty(result.ErrorText) ? result.ExtendedErrorCode.Message : result.ErrorText);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static HttpClient CreateClient()
        {
            // The release list must not come from the HTTP cache, or a new release would be noticed late
            HttpBaseProtocolFilter filter = new HttpBaseProtocolFilter();
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;

            HttpClient client = new HttpClient(filter);
            client.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", userAgent);

            return client;
        }
    }
}
