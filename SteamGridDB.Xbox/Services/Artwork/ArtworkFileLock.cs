using System.Runtime.InteropServices;

namespace SteamGridDB.Xbox.Services.Artwork
{
    /// <summary>
    /// Marks customised artwork files read-only so the Xbox app cannot silently replace or delete them.
    /// </summary>
    /// <remarks>
    /// The Xbox app re-downloads its own artwork for third-party games from time to time (on updates, restarts or
    /// library rescans), which resets customisations. A read-only file survives that. The *FromApp file APIs are the
    /// Win32 functions UWP apps with broadFileSystemAccess are allowed to call on paths outside their package.
    /// </remarks>
    public static class ArtworkFileLock
    {
        private const uint FILE_ATTRIBUTE_READONLY = 0x1;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        [DllImport("api-ms-win-core-file-fromapp-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetFileAttributesFromAppW(string lpFileName, uint dwFileAttributes);

        /// <summary>
        /// Makes the file read-only. Returns false if the attribute could not be set.
        /// </summary>
        public static bool Lock(string filePath)
        {
            return SetAttributes(filePath, FILE_ATTRIBUTE_READONLY);
        }

        /// <summary>
        /// Clears the read-only attribute so the file can be overwritten or deleted. Safe to call on unlocked or missing files.
        /// </summary>
        public static bool Unlock(string filePath)
        {
            return SetAttributes(filePath, FILE_ATTRIBUTE_NORMAL);
        }

        private static bool SetAttributes(string filePath, uint attributes)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return false;
            }

            try
            {
                return SetFileAttributesFromAppW(filePath, attributes);
            }
            catch (System.Exception ex)
            {
                // API set not available on very old Windows builds
                System.Diagnostics.Debug.WriteLine($"Could not change attributes of {filePath}: {ex.Message}");

                return false;
            }
        }
    }
}
