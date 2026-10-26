using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SteamGridDB.Xbox.Services
{
    /// <summary>
    /// Copies Windows Runtime collections into plain lists before they are looped over.
    /// </summary>
    /// <remarks>
    /// Folder and file listings (StorageFolder.GetFoldersAsync / GetFilesAsync) are live views: if the Xbox app or
    /// Steam creates or removes files while a loop that awaits other work is walking one, the next step throws
    /// "Collection was modified; enumeration operation may not execute" (E_CHANGED_STATE). That aborted the whole
    /// library load, e.g. right after inserting an SD card with a Steam library on it. Copying first, and retrying when
    /// the listing changes during the copy, avoids that.
    /// </remarks>
    public static class StorageSnapshot
    {
        private const int attempts = 3;

        /// <summary>
        /// Runs the listing and returns a stable copy, retrying when the listing changed while being copied.
        /// </summary>
        public static async Task<List<T>> GetAsync<T>(Func<Task<IReadOnlyList<T>>> getItems)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return (await getItems()).ToList();
                }
                catch (InvalidOperationException ex) when (attempt < attempts)
                {
                    System.Diagnostics.Debug.WriteLine($"Listing changed while being read (attempt {attempt}): {ex.Message}");

                    await Task.Delay(250 * attempt);
                }
            }
        }

        /// <summary>
        /// Copies a JSON object's entries, so the loop over them is not tied to the object itself.
        /// </summary>
        public static List<KeyValuePair<string, TValue>> Copy<TValue>(IEnumerable<KeyValuePair<string, TValue>> items)
        {
            return items.ToList();
        }
    }
}
