using System;
using System.Threading.Tasks;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SteamGridDB.Xbox.Services.Artwork
{
    /// <summary>
    /// Converts downloaded artwork into an image the Xbox app can always display.
    /// </summary>
    /// <remarks>
    /// Sources return a mix of PNG, JPEG, WebP and ICO files, and not all of them are exactly square.
    /// Saving those bytes as-is under a .png name is what made some changes silently fail to show up in the Xbox app,
    /// so every image is decoded, centre-cropped to a square, scaled down if needed and re-encoded in the format the
    /// target file name implies.
    /// </remarks>
    public static class ArtworkImageConverter
    {
        public const uint DefaultMaxSize = 1024;

        /// <param name="source">Raw downloaded image bytes.</param>
        /// <param name="asJpeg">Encode as JPEG instead of PNG (for target files with a .jpg extension).</param>
        /// <param name="maxSize">Maximum width/height of the output.</param>
        /// <returns>Encoded square image.</returns>
        public static async Task<IBuffer> ToSquareImageAsync(IBuffer source, bool asJpeg, uint maxSize = DefaultMaxSize)
        {
            using (var input = new InMemoryRandomAccessStream())
            {
                await input.WriteAsync(source);
                input.Seek(0);

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);

                // ICO files contain several sizes and the first frame is often the smallest one
                BitmapFrame frame = await decoder.GetFrameAsync(0);

                for (uint i = 1; i < decoder.FrameCount; i++)
                {
                    BitmapFrame candidate = await decoder.GetFrameAsync(i);

                    if (candidate.PixelWidth * candidate.PixelHeight > frame.PixelWidth * frame.PixelHeight)
                    {
                        frame = candidate;
                    }
                }

                uint width = frame.PixelWidth;
                uint height = frame.PixelHeight;
                uint side = Math.Min(width, height);
                uint target = Math.Min(side, maxSize);
                double scale = (double)target / side;
                uint scaledWidth = Math.Max(target, (uint)Math.Round(width * scale));
                uint scaledHeight = Math.Max(target, (uint)Math.Round(height * scale));

                // BitmapTransform scales first, then crops using bounds in the scaled coordinate space
                var transform = new BitmapTransform
                {
                    ScaledWidth = scaledWidth,
                    ScaledHeight = scaledHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant,
                    Bounds = new BitmapBounds
                    {
                        X = (scaledWidth - target) / 2,
                        Y = (scaledHeight - target) / 2,
                        Width = target,
                        Height = target
                    }
                };

                PixelDataProvider pixels = await frame.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                using (var output = new InMemoryRandomAccessStream())
                {
                    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(asJpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, output);

                    encoder.SetPixelData(
                        BitmapPixelFormat.Bgra8,
                        asJpeg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight,
                        target,
                        target,
                        96,
                        96,
                        pixels.DetachPixelData());

                    await encoder.FlushAsync();

                    var buffer = new Windows.Storage.Streams.Buffer((uint)output.Size);
                    output.Seek(0);

                    return await output.ReadAsync(buffer, (uint)output.Size, InputStreamOptions.None);
                }
            }
        }
    }
}
