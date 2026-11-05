using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.System;

namespace SteamGridDB.Xbox.Services.Artwork
{
    /// <summary>
    /// A generated cover made from an app's own icon.
    /// </summary>
    public class AppIconTile
    {
        public string Style { get; set; }

        /// <summary>
        /// ms-appdata URI of the generated PNG, usable both as an image source and as a download source.
        /// </summary>
        public string Uri { get; set; }
    }

    /// <summary>
    /// Builds square covers from an app's own icon, for apps no artwork site knows about (Task Manager, driver tools...).
    /// </summary>
    /// <remarks>
    /// Everything happens on this PC with standard Windows APIs: the shell thumbnail of the program file (what Explorer
    /// shows, up to 256 px) for desktop programs, or the package logo for Store apps. The icon is centred on a tile
    /// background, like the Xbox app's own launcher tiles, instead of being stretched to fill the tile.
    /// </remarks>
    public static class AppIconTiles
    {
        public const string SourceName = "App icon";
        public const string AppDataPrefix = "ms-appdata:///local/";

        private const string tilesFolderName = "AppIconTiles";
        private const uint tileSize = 512;
        private const uint iconBoxSize = 288;
        private const uint requestedIconSize = 256;

        /// <summary>
        /// Generates the tile variants for an app. Returns an empty list when the icon cannot be read.
        /// </summary>
        /// <param name="tileId">Stable identifier for file names (e.g. the custom entry ID).</param>
        /// <param name="launchTarget">Full path of the program, or an app user model ID (Family_Name!App) for Store apps.</param>
        public static async Task<List<AppIconTile>> CreateTilesAsync(string tileId, string launchTarget)
        {
            var tiles = new List<AppIconTile>();

            try
            {
                IRandomAccessStream iconStream = await GetIconStreamAsync(launchTarget);

                if (iconStream == null)
                {
                    return tiles;
                }

                IconPixels icon;

                using (iconStream)
                {
                    icon = await DecodeIconAsync(iconStream);
                }

                if (icon == null)
                {
                    return tiles;
                }

                StorageFolder folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(tilesFolderName, CreationCollisionOption.OpenIfExists);
                string safeId = new string((tileId ?? "app").Where(char.IsLetterOrDigit).ToArray());

                // Old previews of this app are replaced; unique names stop the image cache showing a stale tile
                foreach (StorageFile old in await StorageSnapshot.GetAsync(async () => await folder.GetFilesAsync()))
                {
                    if (old.Name.StartsWith(safeId + "-", StringComparison.OrdinalIgnoreCase))
                    {
                        try { await old.DeleteAsync(); } catch { }
                    }
                }

                string stamp = DateTime.UtcNow.Ticks.ToString();

                // Dark tile like the Xbox app's built-in launchers, and one tinted from the icon's own colours
                var variants = new[]
                {
                    new { Style = "dark", Top = Rgb(0x23, 0x2B, 0x35), Bottom = Rgb(0x10, 0x14, 0x1A) },
                    new { Style = "tinted", Top = Shade(icon.AverageColour, 0.55), Bottom = Shade(icon.AverageColour, 0.22) }
                };

                foreach (var variant in variants)
                {
                    byte[] pixels = Compose(icon, variant.Top, variant.Bottom);
                    string fileName = $"{safeId}-{variant.Style}-{stamp}.png";
                    StorageFile file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

                    using (IRandomAccessStream output = await file.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, tileSize, tileSize, 96, 96, pixels);
                        await encoder.FlushAsync();
                    }

                    tiles.Add(new AppIconTile { Style = variant.Style, Uri = $"{AppDataPrefix}{tilesFolderName}/{fileName}" });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not build icon tiles for {launchTarget}: {ex.Message}");
            }

            return tiles;
        }

        /// <summary>
        /// Reads a generated tile back (for applying it as the app's artwork).
        /// </summary>
        public static async Task<IBuffer> ReadTileAsync(string uri)
        {
            StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));

            return await FileIO.ReadBufferAsync(file);
        }

        private static async Task<IRandomAccessStream> GetIconStreamAsync(string launchTarget)
        {
            if (string.IsNullOrWhiteSpace(launchTarget))
            {
                return null;
            }

            // Store apps are launched by app user model ID: PackageFamilyName!AppId
            int bang = launchTarget.IndexOf('!');

            if (bang > 0 && !launchTarget.Contains("\\"))
            {
                return await GetPackageLogoAsync(launchTarget.Substring(0, bang));
            }

            StorageFile program = await StorageFile.GetFileFromPathAsync(launchTarget);
            StorageItemThumbnail thumbnail = await program.GetThumbnailAsync(ThumbnailMode.SingleItem, requestedIconSize, ThumbnailOptions.ResizeThumbnail);

            return thumbnail;
        }

        private static async Task<IRandomAccessStream> GetPackageLogoAsync(string packageFamilyName)
        {
            // Needs the diagnostic access the user grants once (also used to close the Xbox app)
            if (await AppDiagnosticInfo.RequestAccessAsync() != DiagnosticAccessStatus.Allowed)
            {
                return null;
            }

            IList<AppDiagnosticInfo> infos = await AppDiagnosticInfo.RequestInfoForPackageAsync(packageFamilyName);
            AppDiagnosticInfo info = infos.FirstOrDefault();

            if (info?.AppInfo?.DisplayInfo == null)
            {
                return null;
            }

            RandomAccessStreamReference logo = info.AppInfo.DisplayInfo.GetLogo(new Size(requestedIconSize, requestedIconSize));

            return await logo.OpenReadAsync();
        }

        private class IconPixels
        {
            public uint Width;
            public uint Height;
            public byte[] Bgra;
            public byte[] AverageColour;
        }

        private static async Task<IconPixels> DecodeIconAsync(IRandomAccessStream stream)
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;

            if (width == 0 || height == 0)
            {
                return null;
            }

            // Fit the icon inside the icon box, keeping its aspect ratio
            double scale = Math.Min((double)iconBoxSize / width, (double)iconBoxSize / height);
            uint scaledWidth = Math.Max(1, (uint)Math.Round(width * scale));
            uint scaledHeight = Math.Max(1, (uint)Math.Round(height * scale));

            PixelDataProvider data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform { ScaledWidth = scaledWidth, ScaledHeight = scaledHeight, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            byte[] bgra = data.DetachPixelData();

            // Average of the clearly visible pixels, weighted by opacity
            double b = 0, g = 0, r = 0, weight = 0;

            for (int i = 0; i < bgra.Length; i += 4)
            {
                double alpha = bgra[i + 3] / 255.0;

                if (alpha < 0.5)
                {
                    continue;
                }

                b += bgra[i] * alpha;
                g += bgra[i + 1] * alpha;
                r += bgra[i + 2] * alpha;
                weight += alpha;
            }

            byte[] average = weight > 0
                ? Rgb((byte)(r / weight), (byte)(g / weight), (byte)(b / weight))
                : Rgb(0x40, 0x48, 0x55);

            return new IconPixels { Width = scaledWidth, Height = scaledHeight, Bgra = bgra, AverageColour = average };
        }

        /// <summary>
        /// Draws a vertical gradient background and blends the icon onto its centre. Returns BGRA pixels.
        /// </summary>
        private static byte[] Compose(IconPixels icon, byte[] top, byte[] bottom)
        {
            byte[] tile = new byte[tileSize * tileSize * 4];

            for (uint y = 0; y < tileSize; y++)
            {
                double t = (double)y / (tileSize - 1);
                byte r = Lerp(top[0], bottom[0], t);
                byte g = Lerp(top[1], bottom[1], t);
                byte b = Lerp(top[2], bottom[2], t);

                for (uint x = 0; x < tileSize; x++)
                {
                    long i = (y * tileSize + x) * 4;
                    tile[i] = b;
                    tile[i + 1] = g;
                    tile[i + 2] = r;
                    tile[i + 3] = 255;
                }
            }

            uint offsetX = (tileSize - icon.Width) / 2;
            uint offsetY = (tileSize - icon.Height) / 2;

            for (uint y = 0; y < icon.Height; y++)
            {
                for (uint x = 0; x < icon.Width; x++)
                {
                    long source = (y * icon.Width + x) * 4;
                    double alpha = icon.Bgra[source + 3] / 255.0;

                    if (alpha <= 0)
                    {
                        continue;
                    }

                    long target = ((y + offsetY) * tileSize + (x + offsetX)) * 4;

                    for (int channel = 0; channel < 3; channel++)
                    {
                        tile[target + channel] = (byte)Math.Round(icon.Bgra[source + channel] * alpha + tile[target + channel] * (1 - alpha));
                    }
                }
            }

            return tile;
        }

        private static byte[] Rgb(byte r, byte g, byte b) => new[] { r, g, b };

        private static byte[] Shade(byte[] rgb, double factor) => Rgb((byte)(rgb[0] * factor), (byte)(rgb[1] * factor), (byte)(rgb[2] * factor));

        private static byte Lerp(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
    }
}
