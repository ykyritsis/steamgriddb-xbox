using System.Runtime.Serialization;

namespace SteamGridDB.Xbox.Services.SteamGridDB.Models
{
    /// <summary>
    /// Represents a game search result from SteamGridDB.
    /// </summary>
    [DataContract]
    public class SteamGridDbGame
    {
        [DataMember(Name = "id")]
        public int Id
        {
            get; set;
        }

        [DataMember(Name = "name")]
        public string Name
        {
            get; set;
        }

        [DataMember(Name = "types")]
        public string[] Types
        {
            get; set;
        }

        [DataMember(Name = "verified")]
        public bool Verified
        {
            get; set;
        }

        /// <summary>
        /// Secondary line shown in search results (not part of the API response).
        /// </summary>
        public string Subtitle
        {
            get => subtitle ?? $"SteamGridDB ID: {Id} • Verified: {Verified}";
            set => subtitle = value;
        }

        private string subtitle;

        /// <summary>
        /// True for the synthetic search result that searches store artwork only.
        /// </summary>
        public bool IsStoreSearchOnly => Id == 0;
    }
}
